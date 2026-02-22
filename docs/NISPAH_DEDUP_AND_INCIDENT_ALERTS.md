# Nispah deduplication, accident story annex, and incident alert classification

This document describes Nispah deduplication (idempotency), the accident story annex write flow, cooling mechanism, and how incident email classification and suppression work so that expected duplicate-key violations do not trigger critical alerts.

---

## 1. Nispah deduplication (idempotency)

### Purpose

The **NispahDeduplications** table in Integration DB stores a unique fingerprint per logical “nispah” so that the same content is not written twice. The unique key is **(TikVisualID, NispahTypeName, InfoHash)** (enforced by unique index, e.g. `IX_NispahDeduplications_TikVisualID_NispahTypeName_InfoHash`). This enables:

- **Rescans / retries**: Re-processing the same case and type is treated as a normal skip.
- **Concurrency**: Two concurrent attempts for the same key do not crash the run; the second insert fails with a duplicate-key error and is handled as skip.

### Behavior

- **Insert path**: After a successful Odcanit stored-procedure call, the worker inserts one row into `dbo.NispahDeduplications`. If the row for that (TikVisualID, NispahTypeName, InfoHash) already exists, SQL Server raises a duplicate-key error (see below).
- **Dedup check before write**: The worker also checks this table *before* calling the stored procedure (guardrail “IsDuplicateAsync”). So normally duplicates are skipped before any write. The insert-after-write is still required to record the fingerprint for future runs; and under concurrency or timing, the insert can still see a duplicate.

### Duplicate-key handling (2601 and 2627)

When `SaveChangesAsync` fails due to a **SQL Server unique constraint/index violation**:

- **SQL error 2601**: Duplicate key in unique index.
- **SQL error 2627**: Violation of UNIQUE KEY constraint.

The system treats these as **expected, idempotent “already exists”**:

1. **No exception is rethrown** — the run continues.
2. **No ERROR or exception stack is logged** — only an Information (or Warning) message.
3. **No critical incident email is sent** — severity is not Critical for this scenario.
4. **EF Core change tracker is cleaned** — the entity that failed to insert is detached (`Entry(dedup).State = EntityState.Detached`) so that subsequent `SaveChangesAsync` (e.g. for audit log) is not poisoned by an entity still in `Added` state.

Log message used when a duplicate is detected on insert:

- `"Nispah dedup exists -> skipped | TikVisualID=..., NispahTypeName=..., InfoHash=..."` (Information level).

### Helper methods

- **`NispahWriterService.IsDuplicateKeySqlError(int sqlErrorNumber)`**  
  Returns true for 2601 and 2627. Used internally for idempotent dedup.

- **`NispahWriterService.IsSqlUniqueViolation(DbUpdateException ex)`**  
  Returns true when `ex.InnerException` is a `SqlException` with Number 2601 or 2627. Used to:
  - Decide the duplicate-key catch in `RecordDeduplicationAsync`.
  - In DocumentIngestionService, avoid sending a critical alert when the accident story nispah write fails *only* due to this SQL unique violation (defense in depth).

---

## 2. Accident story annex write flow

### Overview

The **accident story** feature writes a single “סיפור תאונה” (accident story) nispah per case from Monday.com questionnaire data into Odcanit. The flow is idempotent at two levels:

1. **Per-case state flag** (Integration DB): `CaseAnnexWriteState.AccidentStoryAnnexWritten` per TikCounter.
2. **Nispah dedup table**: (TikVisualID, NispahTypeName, InfoHash) in `NispahDeduplications`.

### State flag logic

- **Repository**: `ICaseAnnexWriteStateRepository` / `CaseAnnexWriteStateRepository`.
  - `GetOrCreateStateAsync(tikCounter)`: Returns existing state or creates a new row with `AccidentStoryAnnexWritten = false`.
  - `MarkAccidentStoryWrittenAsync(tikCounter, runId)`: Sets `AccidentStoryAnnexWritten = true`, `AccidentStoryAnnexWrittenAtUtc = DateTime.UtcNow`, and `AccidentStoryAnnexWrittenRunId = runId` after a successful write (or on dedup hit).

- **Flow in DocumentIngestionService**:
  1. For each questionnaire item linked to a case, resolve TikCounter and TikVisualID.
  2. Call `GetOrCreateStateAsync(tikCounter)`. If `state.AccidentStoryAnnexWritten` is already true → **skip** (log “ACCIDENT STORY SKIP | AlreadyWritten=true”).
  3. Compose note text from Monday Q/A columns (AccidentStoryComposer).
  4. Call `NispahWriterService.CreateNispahAsync(tikVisualID, text, nispahType, correlationId, ct)`.
  5. If `CreateNispahAsync` returns true → call `MarkAccidentStoryWrittenAsync(tikCounter, runId)` so the case is not processed again for this annex.

No text-based “marker” or header in the Odcanit note is used for idempotency; the single source of truth for “already written for this case” is the state flag.

### When duplicate-key occurs

If the dedup row already exists (e.g. same case/type processed again or race), the failure happens inside `RecordDeduplicationAsync` (after the SP call and before or during the dedup insert). The duplicate-key exception is caught there, the tracker is cleaned, and the run continues. `CreateNispahAsync` does not throw in this case, so the caller still gets success and can call `MarkAccidentStoryWrittenAsync`. If for any reason a `DbUpdateException` with 2601/2627 did propagate to DocumentIngestionService, the accident story catch block calls **MarkAccidentStoryWrittenAsync** (so the case is marked written and stops re-processing), then returns **without** sending a critical alert (see Incident email classification below).

---

## 3. Cooling mechanism (Israeli business days)

The **cooling period** is used in the **bootstrap onboarding** flow (SyncService_Bootstrap), not in the Nispah/accident story path. It is documented here for completeness as requested.

- **Config**: `Onboarding:CoolingPeriodDays` (default: 3). Number of **Israeli business days** to wait before a case is eligible for onboarding.
- **Israeli business days**: Sunday–Thursday. Friday and Saturday are not counted.
- **Logic**: The day the case is opened (or the first business day if opened on Fri/Sat) counts as day 1. The case becomes eligible only after N full business days (i.e. eligible from the date computed by `AddIsraeliBusinessDays(openDateIsrael, coolingPeriodDays)`).
- **Usage**: Unmapped cases from Odcanit are filtered by this eligibility; only cases past the cooling period are onboarded to Monday.com in the bootstrap run.

---

## 4. Incident email classification and suppression

### Critical alerts

- **DocumentIngestionService** sends critical alerts via `SendAlert(...)`, which calls `_emailNotifier.QueueCriticalAlert(...)` with subject, body, exception type name, and source (e.g. "DocumentIngestionService").
- **DocumentIngestionWorker** sends a critical alert when the worker run crashes (unhandled exception in `ExecuteAsync`).

### Suppression for SQL unique violation (2601/2627)

- **At source (NispahWriterService)**  
  In `RecordDeduplicationAsync`, `SaveChangesAsync` is wrapped in try/catch. When the exception is a `DbUpdateException` and `IsSqlUniqueViolation(ex)` is true:
  - The exception is **not rethrown**.
  - Only an Information log is written (“dedup exists -> skipped”).
  - The dedup entity is detached so the change tracker is clean.
  - Therefore the run does not fail and no critical email is triggered from this path.

- **Defense in depth (DocumentIngestionService)**  
  In the accident story write path, the catch block that would call `SendAlert("Accident story nispah write failed for TikVisualID=...")` first checks:
  - If `ex is DbUpdateException dbEx && NispahWriterService.IsSqlUniqueViolation(dbEx)`:
    - Log a **Warning**: “ACCIDENTSTORY dedup key already exists (skip, no alert) | …”
    - **Do not** call `SendAlert`; return.
  - Otherwise: log Error and call `SendAlert` as before.

So duplicate-key violations (2601/2627) are **never** classified as Critical for the accident story nispah write; they are treated as a normal skip with no incident email.

### Other DbUpdateException

Any other `DbUpdateException` (e.g. FK violation, other SQL errors) is **not** treated as idempotent. Existing behavior is preserved: log as error and send critical alert when in the accident story failure path.

---

## 5. Logging behavior

| Scenario | Log level | Message / behavior |
|--------|-----------|----------------------|
| Dedup insert succeeds | Information | Normal success logs from CreateNispahAsync / audit. |
| Dedup insert fails with 2601/2627 | Information | “Nispah dedup exists -> skipped \| TikVisualID=..., NispahTypeName=..., InfoHash=...” (no exception object, no stack trace). |
| Accident story skipped (state already written) | Information | “ACCIDENT STORY SKIP \| AlreadyWritten=true \| …” |
| Accident story failed with 2601/2627 (defense-in-depth catch) | Warning | “ACCIDENTSTORY dedup key already exists (skip, no alert) \| …” |
| Accident story failed with other exception | Error | “ACCIDENTSTORY FAILED \| …” and SendAlert (critical). |
| NispahDeduplications table missing (208) | Warning | Once per process: “NispahDeduplications table does not exist (SqlException 208). Dedup recording skipped. …” |

---

## 6. Tests

- **NispahWriterServiceTests**:
  - `IsDuplicateKeySqlError_2601_ReturnsTrue`, `IsDuplicateKeySqlError_2627_ReturnsTrue`, `IsDuplicateKeySqlError_OtherNumbers_ReturnsFalse`.
  - `IsSqlUniqueViolation_DbUpdateWith2601_ReturnsTrue`, `IsSqlUniqueViolation_DbUpdateWith2627_ReturnsTrue`, `IsSqlUniqueViolation_DbUpdateWith208_ReturnsFalse`, `IsSqlUniqueViolation_NullOrNonSqlInner_ReturnsFalse`.
- **AccidentStoryIdempotencyTests**: State repository and “already written” behavior (e.g. `MarkAccidentStoryWrittenAsync_ThenGetOrCreate_ReturnsWrittenTrue`).
- **CoolingPeriodTests**: Israeli business days and cooling eligibility.

---

## 7. Migrations / entities

- **NispahDeduplications** (and NispahAuditLogs): Migration `20260220112714_AddNispahDedupAndAuditTables`. Unique index on (TikVisualID, NispahTypeName, InfoHash) (or equivalent) enforces one row per fingerprint.
- **CaseAnnexWriteState**: Migration `20260222155607_AddCaseAnnexWriteState`. Table `CaseAnnexWriteState` with TikCounter (PK) and `AccidentStoryAnnexWritten` (and optional RunId). Used for per-case “accident story annex already written” flag.

---

## 8. Changes introduced on 20/02–22/02

- **Nispah dedup idempotency**
  - Duplicate-key (SQL 2601 and 2627) on insert into `NispahDeduplications` is caught in `RecordDeduplicationAsync`, logged as Information (“dedup exists -> skipped”), and not rethrown.
  - EF change tracker cleanup: the failed dedup entity is detached after the duplicate-key catch so subsequent `SaveChangesAsync` (e.g. audit log) is not poisoned.
  - Helper `IsSqlUniqueViolation(DbUpdateException ex)` added; duplicate-key catch uses it in the when clause.

- **Accident story annex**
  - Idempotency via `CaseAnnexWriteState` (GetOrCreateStateAsync, MarkAccidentStoryWrittenAsync). No Monday.com config changes; no Odcanit DB schema changes.
  - Accident story write flow: state check → compose → CreateNispahAsync → on success MarkAccidentStoryWrittenAsync.

- **Incident email suppression**
  - Critical alert is **not** sent when the only failure is a SQL unique violation (2601/2627) on the accident story nispah path. Handled in NispahWriterService (no throw) and in DocumentIngestionService catch (IsSqlUniqueViolation → log Warning, no SendAlert).

- **Cooling**
  - Bootstrap onboarding uses Israeli business days (Sunday–Thursday); cooling period configured by `Onboarding:CoolingPeriodDays` (default 3).

- **Tests**
  - IsDuplicateKeySqlError and IsSqlUniqueViolation unit tests added in NispahWriterServiceTests. Accident story state and cooling tests in existing test classes.

- **Repeated annex fix (dedup-hit path)**
  - When a SQL unique violation (2601/2627) propagates to DocumentIngestionService (accident story catch), the handler now calls **MarkAccidentStoryWrittenAsync(tikCounter, runId)** before returning, so the case is marked written and the worker will skip it on subsequent runs (stops repeated annex writes). State columns set: AccidentStoryAnnexWritten=1, AccidentStoryAnnexWrittenAtUtc=UtcNow, AccidentStoryAnnexWrittenRunId=runId.

- **Documentation**
  - This file (NISPAH_DEDUP_AND_INCIDENT_ALERTS.md) added. INTEGRATIONDB_MIGRATIONS.md and ODMON_Overview.md updated with references to Nispah dedup and this doc.
