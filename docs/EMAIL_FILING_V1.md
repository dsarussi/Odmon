# ODMON Email Filing V1

## Production authority and safety

TikNumber is the only V1 filing authority. Candidates are extracted from the
lightweight Graph subject and body, then validated by exact TikNumber equality
in Odcanit. Court/proceeding-number detection is observer diagnostics only and
can never add a filing target. RE/FW/FWD messages remain eligible.

EmailFiling is isolated from EmailAutomation forwarding and disabled by
default. A real write requires every gate below:

- `EmailFiling:Enabled=true`
- `EmailFiling:DryRun=false`
- `EmailFiling:RealWriteEnabled=true`
- exact TikNumber resolution
- either `AllowAllResolvedTikNumbers=true` or an exact TikNumber/TikCounter allowlist pair
- no successful email-fingerprint/TikCounter dedup record

The initial allowlist contains only TikNumber `9/1984`, TikCounter `40514`.
`AllowAllResolvedTikNumbers` defaults to false. Empty, missing, or malformed
allowlist configuration never enables unrestricted writing. When the explicit
setting is true, only uniquely resolved internal TikNumbers gain authority;
unresolved, ambiguous, and court-only identifiers remain non-fileable.

## Filing flow

```text
independent Graph delta polling
  -> lightweight subject/body retrieval
  -> complete TikNumber extraction
  -> exact Odcanit TikNumber resolution
  -> safety, allowlist, and successful-dedup checks
  -> full RFC 822 MIME retrieval (only when at least one target is writable)
  -> MIME -> neutral email model
  -> MsgKit -> temporary .msg
  -> validate absolute .msg path, non-empty length, and CFB signature
  -> reserve keyed email-fingerprint/TikCounter operation in IntegrationDb
  -> OdcanitDocumentWriter / dbo.ProcDocuments_AddNewDocument
  -> persist returned DocCounter only
  -> validate the returned/resolved path against configured protected roots
  -> copy to returned protected Documents path
  -> verify destination exists, exact byte length, and CFB signature
  -> mark the reservation succeeded
```

The lightweight delta query never expands or downloads attachments. Full MIME
is fetched once only after at least one resolved target passes every production
gate. One neutral model and one temporary MSG are then reused for all eligible
TikCounters.

For each target, `OdcanitDocumentWriter` uses the existing
`OdcanitDocuments` category, subcategory, status, writer, owner, metapel, and
document-type conventions. The `.msg` source path is passed unchanged so
Odcanit's existing extension mapping remains authoritative; EmailFiling does
not hardcode DocType 9. The sanitized email subject is the document name, and
the best available sent/received email timestamp is passed as `CreateDate`.
Odcanit continues to control `tsCreateDate`.

This slice writes no Nispah and does not write `dbo.MailsDocsParams`.

## Multi-target, dedup, and retry

Distinct TikNumbers resolving to the same TikCounter collapse to one target.
Distinct resolved TikCounters remain independent targets. MIME is fetched once
and MSG is generated once, then the same logical MSG is written to each
eligible target.

`EmailFilingDedups` has a unique email-fingerprint/TikCounter key and is a
durable write-state record as well as the final success proof. It is reserved
before the Odcanit procedure call. After the procedure returns, only its
DocCounter is persisted before copying. On retry, the existing verified
`dbo.procDocumentsGroup_BuildDocPath` read-only path lookup resolves the
protected `.msg` destination from DocCounter. The path is canonicalized and
must remain beneath `AllowedDestinationRoots`. Copy failures retry against that
same Odcanit row. If the physical copy completed but the final success update
did not, replay resolves and verifies the same destination and marks the
reservation succeeded without creating another row.

`CREATING_DOCUMENT` and `CREATE_UNCERTAIN` are deliberately not retried. A
process failure after the create intent is persisted but before DocCounter is
durably recorded cannot be resolved through the verified Odcanit
contract. Such a state raises a privacy-minimized critical alert for manual
repair. No supported Documents rollback/delete contract exists in this
codebase, so EmailFiling does not invent one.

## Temporary files and privacy

MSG files are generated beneath the process temporary directory in the
`odmon-email-filing` subdirectory with a random GUID filename. The artifact is
disposed in all success and failure paths and deletion is best-effort. The
temporary filename contains no subject, sender, recipient, case number,
Message-ID, or Graph ID.

Diagnostics contain keyed fingerprints, candidates, exact resolutions, target
decisions, counts, and observer classifications. They do not contain bodies,
attachment content, sender/recipient content, Internet Message-ID, or raw Graph
message IDs. Operational logs avoid those values as well.

Full MIME and Graph delta responses have positive, fail-closed size limits.
MIME attachment count and identifier-candidate count are also bounded, and
persisted diagnostic identifiers are limited to 64 characters. ODMON does not
render HTML, fetch remote HTML resources, execute attachments, or extract
archives. Attachment filenames are reduced to sanitized leaf names before they
are stored as MSG properties.

## TikNumber extraction

```text
(?<![0-9/])[0-9]+/[0-9]+(?![0-9/])
```

The expression consumes the complete numeric token on each side of `/`, allows
adjacent text and trailing punctuation, and rejects partial matches inside
dates or multi-slash values. Every candidate is validated by exact Odcanit
lookup before it can become a target.

## Configuration

```json
"EmailFiling": {
  "Enabled": false,
  "DryRun": true,
  "RealWriteEnabled": false,
  "AllowAllResolvedTikNumbers": false,
  "ResolutionPhantomEnabled": false,
  "IntervalMinutes": 3,
  "MaxMessagesPerCycle": 50,
  "MaxMimeMessageBytes": 52428800,
  "MaxDeltaPageBytes": 52428800,
  "MaxMimeAttachmentCount": 100,
  "MaxIdentifierCandidates": 100,
  "StartProcessingFromUtc": null,
  "AllowHistoricalBackfill": false,
  "AllowedDestinationRoots": [
    "\\\\dc22\\Odlight\\Docs\\",
    "D:\\Odlight\\Docs\\"
  ],
  "RealWriteAllowlist": [
    {
      "TikNumber": "9/1984",
      "TikCounter": 40514
    }
  ]
}
```

With no existing cursor, null `StartProcessingFromUtc` initializes at the
current service time. A past start time is rejected unless
`AllowHistoricalBackfill=true`. Existing cursor URLs are resumed only after
their HTTPS host, mailbox, folder, delta path, and opaque token are validated.
Invalid cursors fail closed and are never reset automatically.

For unrestricted exact-Tik real-write, set `Enabled=true`, `DryRun=false`,
`RealWriteEnabled=true`, and `AllowAllResolvedTikNumbers=true`. The allowlist
may then be empty, but if supplied it must still be well-formed. Court-number
resolution remains observer-only in every authority mode.

EmailFiling uses the existing production `EmailAutomation` Graph configuration
and `OdcanitDocuments` writer identity/configuration. It adds no credentials or
secret storage.

Apply migration `20260827124505_AddEmailFilingV1` before enabling the worker.
Set `StartProcessingFromUtc` explicitly to observe older mail; null establishes
a new current-time baseline.

## Resolution phantom rollout

`ResolutionPhantomEnabled` defaults to false and is independent of
`RealWriteEnabled`. When enabled, the canonical resolver analyzes each message
and stores one privacy-minimized `EmailFilingResolutionRuns` row plus target-set
rows in `EmailFilingResolutionTargets`. An observer target is marked
`PHANTOM_WOULD_FILE`; the existing exact-Tik target set is recorded separately
as `EXISTING_AUTHORITY`. Phantom counters never enter production target
diagnostics, dedup reservations, MIME retrieval, MSG generation, or the Odcanit
writer.

The primary evidence mask is Tik=1, Claim=2, Court=4. The supporting mask is
Vehicle=1, EventDate=2, ClientHint=4, InsuredName=8, DriverPhone=16. Only masks,
counts, enum-like outcomes, TikCounters, safe exception type, and aggregate
timings are stored. Raw evidence values and email content are not stored in the
phantom tables. The run is attached to the existing EmailFiling diagnostic and
saved in the same transaction, so phantom mode adds no separate per-message
diagnostic save.

Apply migration `20260830154754_AddEmailFilingResolutionPhantom` before setting
`ResolutionPhantomEnabled=true`. Leave the office's existing production
EmailFiling settings unchanged and enable only the phantom flag. Phantom mode
does not grant new filing authority and does not require disabling the working
TikNumber filing path; Claim, Court, and supporting evidence remain
observer-only.

For the current production deployment, this produces the following shape. It
describes that deployment and is not a universal default for other offices:

```json
"EmailFiling": {
  "Enabled": true,
  "DryRun": false,
  "RealWriteEnabled": true,
  "AllowAllResolvedTikNumbers": true,
  "ResolutionPhantomEnabled": true
}
```

### Phantom review SQL

```sql
USE odmonintegration;

-- 1. Emails analyzed.
SELECT COUNT_BIG(*) AS EmailsAnalyzed
FROM dbo.EmailFilingResolutionRuns;

-- 2. Final outcome distribution.
SELECT FinalResolutionClass, COUNT_BIG(*) AS EmailCount
FROM dbo.EmailFilingResolutionRuns
GROUP BY FinalResolutionClass
ORDER BY EmailCount DESC;

-- 3. Agreement distribution and exact-agreement rate where Tik authority existed.
SELECT
    AgreementWithExistingAuthority,
    COUNT_BIG(*) AS EmailCount
FROM dbo.EmailFilingResolutionRuns
GROUP BY AgreementWithExistingAuthority
ORDER BY EmailCount DESC;

SELECT
    CAST(100.0 * SUM(CASE WHEN AgreementWithExistingAuthority = 'EXACT_AGREEMENT' THEN 1 ELSE 0 END)
         / NULLIF(COUNT_BIG(*), 0) AS decimal(6,2)) AS ExactAgreementPercent
FROM dbo.EmailFilingResolutionRuns
WHERE ExistingAuthorityTargetCount > 0;

-- 4. Conflicts and disjoint target sets.
SELECT COUNT_BIG(*) AS ConflictOrDisjointCount
FROM dbo.EmailFilingResolutionRuns
WHERE FinalResolutionClass IN ('PRIMARY_CONFLICT', 'SUPPORTING_CONFLICT')
   OR AgreementWithExistingAuthority = 'DISJOINT';

-- 5. Unique phantom resolution with no existing Tik authority.
SELECT COUNT_BIG(*) AS UniquePhantomWithoutExistingTikCount
FROM dbo.EmailFilingResolutionRuns
WHERE ExistingAuthorityTargetCount = 0
  AND PhantomTargetCount = 1
  AND AgreementWithExistingAuthority = 'NO_EXISTING_TIK_AUTHORITY';

-- 6. Ambiguous primary narrowed to unique.
SELECT COUNT_BIG(*) AS NarrowedToUniqueCount
FROM dbo.EmailFilingResolutionRuns
WHERE FinalResolutionClass = 'NARROWED_TO_UNIQUE';

-- 7. Aggregate performance.
SELECT
    AVG(CAST(ExtractionDurationMs AS decimal(18,2))) AS AvgExtractionMs,
    MAX(ExtractionDurationMs) AS MaxExtractionMs,
    AVG(CAST(PrimaryResolutionDurationMs AS decimal(18,2))) AS AvgPrimaryResolutionMs,
    MAX(PrimaryResolutionDurationMs) AS MaxPrimaryResolutionMs,
    AVG(CAST(SupportingNarrowingDurationMs AS decimal(18,2))) AS AvgSupportingNarrowingMs,
    MAX(SupportingNarrowingDurationMs) AS MaxSupportingNarrowingMs,
    AVG(CAST(TotalPhantomDurationMs AS decimal(18,2))) AS AvgTotalPhantomMs,
    MAX(TotalPhantomDurationMs) AS MaxTotalPhantomMs
FROM dbo.EmailFilingResolutionRuns;

-- 8. Observer errors by safe exception category.
SELECT ObserverErrorCategory, COUNT_BIG(*) AS ErrorCount
FROM dbo.EmailFilingResolutionRuns
WHERE FinalResolutionClass = 'OBSERVER_ERROR'
GROUP BY ObserverErrorCategory
ORDER BY ErrorCount DESC;
```

## Observer verification SQL

```sql
USE odmonintegration;

SELECT TOP (200)
    Id, CreatedAtUtc, Mailbox, ReceivedDateTimeUtc, MessageFingerprint,
    TikCandidateCount, ResolvedTikCount, SuspectNotCaseCount,
    CourtCandidateCount, ResolvedCourtCount, TargetCount, DedupHitCount,
    ObserverClassifications, FinalDecision
FROM dbo.EmailFilingDiagnostics
ORDER BY Id DESC;
```

```sql
USE odmonintegration;

SELECT TOP (500)
    d.Id AS DiagnosticId, d.CreatedAtUtc, c.CandidateType, c.Source,
    c.Candidate, c.ResolutionStatus, c.ResolvedTikNumber,
    c.ResolvedTikCounter
FROM dbo.EmailFilingCandidateDiagnostics AS c
JOIN dbo.EmailFilingDiagnostics AS d
  ON d.Id = c.EmailFilingDiagnosticId
ORDER BY c.Id DESC;
```

```sql
USE odmonintegration;

SELECT TOP (500)
    d.Id AS DiagnosticId, d.CreatedAtUtc, d.MessageFingerprint,
    t.TikNumber, t.TikCounter, t.RealWriteAllowlisted,
    t.DedupResult, t.Decision, d.FinalDecision
FROM dbo.EmailFilingTargetDiagnostics AS t
JOIN dbo.EmailFilingDiagnostics AS d
  ON d.Id = t.EmailFilingDiagnosticId
ORDER BY t.Id DESC;
```

```sql
USE odmonintegration;

SELECT TOP (200)
    Id, MessageFingerprint, TikNumber, TikCounter, Status,
    OdcanitDocCounter, ExpectedFileLength,
    LastErrorCategory, CreatedAtUtc, UpdatedAtUtc, FiledAtUtc
FROM dbo.EmailFilingDedups
ORDER BY Id DESC;
```
