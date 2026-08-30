# ODMON Email Filing Resolution Engine — SPEC v0.2

**Status:** Draft validated against initial production DB sampling

## 1. Product goal

Build a deterministic, secure, portable email-to-case resolution engine that can be reused across Odcanit-based law firms without rewriting the filing pipeline.

Priorities:
1. No wrong filing.
2. High automatic filing coverage.
3. Deterministic, explainable decisions.
4. Minimal retention of personal data.
5. Reuse across firms through adapters/configuration rather than forks.
6. Preserve the existing Graph/MIME/MSG/dedup/recovery/Odcanit write pipeline.

A missed automatic filing is an operational inconvenience. A wrong automatic filing is a data-integrity/confidentiality incident. Ambiguity therefore fails closed.

## 2. Architecture

Email → Extraction → Normalization → Odcanit Resolution → Evidence Engine → Authority Policy → TikCounter target(s) → Existing EmailFiling execution pipeline

## 3. Canonical extraction model

Every email is processed into the same canonical model, with multiple candidates allowed per field:

- InternalTikNumbers[]
- ClaimNumbers[]          // business meaning: מספר תביעה = זיהוי נוסף
- CourtCaseNumbers[]      // business meaning: מספר הליך = מספר תיק בית משפט
- VehicleNumbers[]
- InsuredNames[]
- DriverPhones[]
- EventDates[]
- ClientHints[]

Each candidate should carry transient provenance:
- normalized value
- subject/body source
- explicit-label / structured-pattern / contextual extraction kind
- optional context group

Raw personal-data evidence should not be persisted merely for diagnostics.

## 4. Evidence classes

### Primary identifiers
- Internal TikNumber
- Claim Number / Additional (`מספר תביעה` = `זיהוי נוסף`)
- Court Case Number (`מספר הליך` = `מספר תיק בית משפט`)

These can create filing authority only after deterministic Odcanit resolution.

### Supporting evidence
- Vehicle number
- Insured name
- Driver phone
- Event date
- Client identity where reliably available

**Policy number is out of scope for EmailFiling resolution.** It should not be extracted or used as resolver evidence in V1.

These primarily corroborate/disambiguate. They are not standalone filing keys in V1.

## 5. Key principle: extraction is not resolution

`מספר תביעה: X` can strongly prove that X is a claim number, but it does not prove which case receives the email if X exists on several cases.

Every resolver returns:
- NoMatch
- Unique
- Ambiguous
plus its candidate TikCounters.

No resolver may silently choose first/newest/lowest when several distinct TikCounters match.

## 6. Verified Odcanit findings

### Claim number
Verified production source:
- `vwHozlapFormsData_TikMainData.Counter`
- `vwHozlapFormsData_TikMainData.VisualId`
- `vwHozlapFormsData_TikMainData.Additional`
- `vwHozlapFormsData_TikMainData.clcCourtTikNum`

Production checks found **2,277 distinct `Additional` values** mapping to more than one Counter. A sampled value mapped to six cases.

Therefore `Additional match != resolved case`.

### Court case number
Production checks found **273 `clcCourtTikNum` values** mapping to more than one Counter. The field also contains dirty/non-court values, so format validation is mandatory.

Manual sampling showed an important positive pattern: duplicate ClaimNumber/Additional values that remained ambiguous even after Client + EventDate + Vehicle could still map to different court case numbers. Therefore CourtCaseNumber is both an independent strong identifier and a powerful disambiguator for duplicate claim numbers.

### Client relation
A sampled production join confirmed:
`SIDES.SideDataCounter -> vwExportToOuterSystems_Clients.SideCounter`

This can yield ClientVisualID/ClientName. However, one duplicate claim remained duplicated even within the same client, so client is supporting evidence, not a universal solution.

### Legacy-looking VisualIds

Some older Odcanit case numbers contain dots (for example `94/1.7076`). These cases may often be old or operationally irrelevant, but this is **not a safe exclusion rule**. The resolver must not discard dotted VisualIds unless a future explicit business rule is verified and approved.

### Initial production evidence-strength findings

Read-only production sampling produced these useful signals:

- 2,277 distinct `Additional` values map to more than one case.
- 273 `clcCourtTikNum` values map to more than one case.
- For duplicate claim numbers, `Claim + Vehicle` produced 692 unique combinations out of 1,925 combinations checked.
- `Claim + EventDate` produced 830 unique combinations out of 1,856.
- `Claim + Vehicle + EventDate` produced 867 unique combinations out of 1,859.
- `Claim + EventDate + Client` produced 1,725 unique combinations out of 2,320.

These are combination-level exploratory measurements, not email-level production success rates. They justify the multi-evidence resolver design but must not be marketed or reported as automatic-filing coverage.

### Supporting fields already documented in ODMON
Existing project mapping documents identify:
- Event date
- Main vehicle number
- Policy-holder name
- Driver phone

Their exact EmailFiling lookup contracts must still be verified against current code/schema before implementation.

### Policy number
Policy number was explicitly removed from EmailFiling resolution scope. `vwInsurance` exists and exposes policy-related columns, but it is not populated for all relevant cases and policy number is not required for the business resolver. Do not spend implementation scope on it.

## 7. Resolution model

Each extracted value produces a candidate set.

Example:
Claim X -> {A,B,C}
Vehicle Y -> {B,D}
EventDate Z -> {B,E}

Intersection -> {B}

That is a deterministic resolution.

## 8. Proposed V1 authority rules

### A. No primary identifier
Supporting evidence alone does not authorize filing in V1.

The engine may extract Vehicle / EventDate / Client / InsuredName / DriverPhone for diagnostics and future analysis, but they do not independently create filing authority.

### B. One primary identifier resolves uniquely
Authorize only if no other primary identifier contradicts it.

### C. Primary identifier is ambiguous
Use supporting evidence to reduce its candidate set. Authorize only if deterministic filtering leaves exactly one TikCounter.

### D. Primary identifiers agree
If independent primary identifiers resolve to the same TikCounter, authorize.

A CourtCaseNumber may also disambiguate an ambiguous ClaimNumber by intersection:
`Claim -> {A,B,C,D}`, `CourtCaseNumber -> {C}` => authorize C.

### E. Primary identifiers conflict
Fail closed. Do not hide the contradiction with a score or priority rule.

### F. Multiple explicit internal TikNumbers
Preserve the existing approved multi-target behavior for independently exact-resolved internal TikNumbers.

Other multi-primary/multi-case cases remain fail-closed until context grouping is explicitly designed and tested.

## 9. No scoring model

Do not implement arbitrary weights such as:
Claim=80, Vehicle=40, Name=20.

Scores can hide contradictions.

Use candidate sets, exact agreement, intersection, ambiguity and conflict states.

## 10. Thread and multi-case handling

Email threads can contain quoted history, several claim numbers, several vehicles or several cases.

Therefore:
- preserve multiple candidates and provenance;
- do not globally intersect unrelated values blindly;
- design for optional `ContextGroup` so evidence near the same anchor identifier can later be evaluated together;
- until grouping is proven, ambiguous multi-primary cases fail closed, except existing explicit multi-Tik behavior.

## 11. Privacy and diagnostics

Persist only what is operationally necessary, such as:
- fingerprint
- TikCounter targets
- decision
- resolver types present
- candidate counts
- matched evidence types
- ambiguity/conflict reason
- dedup/write state

Avoid persisting just for diagnostics:
- names
- phone numbers
- vehicle numbers
- event dates
- raw subject/body
- attachment content
- raw Graph IDs / Message-ID

Detailed personal-data evidence should normally exist only in memory during processing.

## 12. Portability

Separate three layers:

### Core engine
Canonical fields, normalization, candidate-set logic, authority rules, conflict handling, diagnostics.

### Odcanit adapter
Exact views/columns/joins and case lookup queries.

### Office configuration
Enabled identifiers, label synonyms, sender/domain hints and office/client mappings.

Avoid per-office forks unless the underlying data contract truly differs.

## 13. Rollout

### Phase 0 — schema/code audit
Verify every field source and join against current repository and production-safe read-only queries.

### Phase 1 — canonical extraction
Introduce the new model without changing filing authority.

### Phase 2 — shadow resolution
Run Claim/Court/supporting resolvers in observer mode alongside current production authority.

Measure unique, ambiguous, conflict, no-match and agreement rates.

### Phase 3 — authority validation
Use real production samples and curated tests.

### Phase 4 — gated production authority
Enable additional resolver classes deliberately, one verified class at a time.

### Phase 5 — portable packaging
Move office-specific mappings/synonyms behind adapters/config.


## 13.1 Mandatory phantom/shadow validation before new authority

The upgraded resolver must run for several real production days in phantom/shadow mode before ClaimNumber/CourtCaseNumber/supporting evidence can authorize writes.

Shadow telemetry should capture decision structure without persisting raw PII:

- fields/types found
- number of candidates per resolver
- primary identifiers present
- resolution path
- agreement / conflict / ambiguity class
- would-file TikCounter(s)
- would-not-file reason
- whether existing InternalTik authority agreed with the new resolver when both were available

The shadow period is part of Definition of Done, not an optional rollout convenience. Its purpose is to expose real email variety: forwards, replies, quoted history, malformed numbers, missing labels, multiple cases, stale identifiers and unexpected client-specific wording.

## 14. Required test matrix

Extraction:
- explicit Tik
- explicit claim
- unlabeled claim-like number
- court number
- date false positives
- phone/vehicle normalization
- Hebrew punctuation/spacing
- reply/forwarded content
- duplicate values
- several values of same type

Resolution:
- 0 / 1 / many candidate cases
- duplicate DB rows for same TikCounter
- same identifier -> several TikCounters
- dirty court values
- same claim across clients

Evidence:
- ambiguous claim + unique vehicle
- ambiguous claim + date only
- claim + court agreement
- claim + court conflict
- unique primary + supporting contradiction
- multiple supporting signals converging
- empty intersection

Execution regressions:
Existing Graph/MIME/MSG/dedup/recovery/write tests must continue passing.

## 15. Open items before implementation

1. Verify the current internal TikNumber lookup contract from the current repository.
2. Verify exact lookup queries for vehicle, insured name, driver phone and event date against current implementation/schema.
3. Define the exact Client resolver contract using the confirmed SIDES -> Clients relationship, including which SideTypes represent the business client.
4. Define normalization contracts for every canonical field.
5. Define CourtCaseNumber format validation before database lookup.
6. Define quoted-thread/context grouping behavior.
7. Decide whether a unique primary identifier with contradictory supporting evidence is blocked; conservative default: fail closed until measured.
8. Verify privacy-minimizing diagnostics after expansion.
9. Audit current code against this SPEC and identify the smallest safe extension path without changing the existing filing execution semantics.
10. After implementation, run the new resolver in phantom/shadow mode for several days before granting new authority.

## 16. Definition of done

Ready for broader production authority only when:
- every authoritative field has a verified Odcanit lookup contract;
- ambiguity is never silently selected;
- primary conflicts fail closed;
- supporting evidence follows explicit deterministic rules;
- shadow production data validates behavior;
- unit/integration/regression/end-to-end tests pass;
- diagnostics explain decisions without unnecessary PII retention;
- existing reliable filing execution semantics remain intact;
- office-specific differences can be configured without rewriting the core engine.
