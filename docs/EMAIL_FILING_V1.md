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
- exact TikNumber/TikCounter allowlist pair
- no successful email-fingerprint/TikCounter dedup record

The initial allowlist contains only TikNumber `9/1984`, TikCounter `40514`.

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
  -> persist returned DocCounter and DestPath
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
before the Odcanit procedure call. After the procedure returns, its DocCounter
and DestPath are persisted before copying. Copy failures retry against that
same Odcanit row. If the physical copy completed but the final success update
did not, replay verifies the persisted destination and marks the reservation
succeeded without creating another row.

`CREATING_DOCUMENT` and `CREATE_UNCERTAIN` are deliberately not retried. A
process failure after the create intent is persisted but before DocCounter and
DestPath are durably recorded cannot be resolved through the verified Odcanit
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
  "IntervalMinutes": 3,
  "MaxMessagesPerCycle": 50,
  "StartProcessingFromUtc": null,
  "RealWriteAllowlist": [
    {
      "TikNumber": "9/1984",
      "TikCounter": 40514
    }
  ]
}
```

EmailFiling uses the existing production `EmailAutomation` Graph configuration
and `OdcanitDocuments` writer identity/configuration. It adds no credentials or
secret storage.

Apply migration `20260827124505_AddEmailFilingV1` before enabling the worker.
Set `StartProcessingFromUtc` explicitly to observe older mail; null establishes
a new current-time baseline.

## Observer verification SQL

```sql
SELECT TOP (200)
    Id, CreatedAtUtc, Mailbox, ReceivedDateTimeUtc, MessageFingerprint,
    TikCandidateCount, ResolvedTikCount, SuspectNotCaseCount,
    CourtCandidateCount, ResolvedCourtCount, TargetCount, DedupHitCount,
    ObserverClassifications, FinalDecision
FROM dbo.EmailFilingDiagnostics
ORDER BY Id DESC;
```

```sql
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
SELECT TOP (200)
    Id, MessageFingerprint, TikNumber, TikCounter, Status,
    OdcanitDocCounter, OdcanitDestPath, ExpectedFileLength,
    LastErrorCategory, CreatedAtUtc, UpdatedAtUtc, FiledAtUtc
FROM dbo.EmailFilingDedups
ORDER BY Id DESC;
```
