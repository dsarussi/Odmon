# Changelog — changes around 2026-02-20

Concise summary of recent changes: accident story idempotency, dedup handling, incident alert suppression, and documentation.

---

## Accident story annex — stop repeated writes

- **Per-case state:** `CaseAnnexWriteState` (Integration DB) stores `AccidentStoryAnnexWritten`, `AccidentStoryAnnexWrittenAtUtc`, `AccidentStoryAnnexWrittenRunId` per TikCounter. After a successful accident story nispah write, ODMON sets these via `MarkAccidentStoryWrittenAsync(tikCounter, runId)` so the case is skipped on future runs.
- **Dedup-hit path:** When the dedup insert fails with SQL Server unique violation (2601 or 2627), the handler now **also** calls `MarkAccidentStoryWrittenAsync(tikCounter, runId)` so the case is marked written and the worker stops re-processing it (fixes repeated annexes when the dedup row existed but the state flag was never set).
- **Success path:** On successful `CreateNispahAsync`, the service already called `MarkAccidentStoryWrittenAsync`; no change.

---

## Dedup / idempotency (NispahDeduplications)

- **Unique key:** (TikVisualID, NispahTypeName, InfoHash). Duplicate insert raises SQL 2601 (unique index) or 2627 (unique constraint).
- **Handling:** Caught in `NispahWriterService.RecordDeduplicationAsync`; log Information (“Nispah dedup exists -> skipped”), detach the failed entity so the change tracker is clean, do not rethrow. Run continues; no critical alert.
- **Helper:** `IsSqlUniqueViolation(DbUpdateException ex)` detects 2601/2627. Used in NispahWriterService and in DocumentIngestionService (accident story catch) to classify and suppress critical alerts.

---

## Email / incident monitoring

- **Suppression:** SQL unique violation (2601/2627) on the accident story nispah path is **not** treated as Critical. No critical incident email is sent; log Warning (“dedup key already exists (skip, marked written)”).
- **Defense in depth:** If a `DbUpdateException` with 2601/2627 propagates to DocumentIngestionService, the catch block calls `MarkAccidentStoryWrittenAsync`, logs, and returns without calling `SendAlert`.
- **Other failures:** Any other `DbUpdateException` or exception still triggers existing error logging and critical alert behavior.

---

## Cooling period (bootstrap)

- **Config:** `Onboarding:CoolingPeriodDays` (default: 3). Israeli business days (Sunday–Thursday).
- **Behavior:** Unchanged; used in bootstrap onboarding to delay mapping new cases to Monday.com until after the cooling period. See `SyncService_Bootstrap` and `docs/NISPAH_DEDUP_AND_INCIDENT_ALERTS.md`.

---

## Config keys (reference)

| Key | Purpose |
|-----|---------|
| `Onboarding:CoolingPeriodDays` | Israeli business days before a case is eligible for bootstrap onboarding (default: 3). |
| `MondayDocumentIngestion:Enabled` | Enable/disable DocumentIngestionWorker. |
| `MondayDocumentIngestion:BoardId`, `IntervalSeconds` | Board and poll interval. |
| Accident story columns / write | Per existing appsettings (e.g. accident story column IDs, WriteEnabled). No new keys added for this changelog. |

---

## Operational notes

- **Integration DB:** Ensure migrations are applied so `NispahDeduplications`, `NispahAuditLogs`, and `CaseAnnexWriteState` exist. See `docs/INTEGRATIONDB_MIGRATIONS.md`.
- **Existing cases with multiple annexes:** For cases already showing multiple “סיפור תאונה” annexes, the next run will either succeed and set the state flag, or hit the dedup and set the flag in the catch; subsequent runs will skip (AlreadyWritten=true).
- **No Monday.com or Odcanit DB schema changes** in this set of changes.

---

## Files modified (reference)

- **Services/NispahWriterService.cs** — RecordDeduplicationAsync: catch 2601/2627, detach entity, `IsSqlUniqueViolation(DbUpdateException)`.
- **Services/DocumentIngestionService.cs** — Accident story catch: on `IsSqlUniqueViolation`, call `MarkAccidentStoryWrittenAsync`, then return (no SendAlert).
- **Services/CaseAnnexWriteStateRepository.cs** — `MarkAccidentStoryWrittenAsync` already sets AccidentStoryAnnexWritten, AccidentStoryAnnexWrittenAtUtc, AccidentStoryAnnexWrittenRunId.
- **Models/CaseAnnexWriteState.cs** — Already has the three columns.
- **Odmon.Worker.Tests/NispahWriterServiceTests.cs** — Tests for `IsSqlUniqueViolation`, `IsDuplicateKeySqlError`.
- **Odmon.Worker.Tests/AccidentStoryIdempotencyTests.cs** — `DedupHit_MarkWritten_SetsFlagAndStopsReprocessing`.
- **docs/NISPAH_DEDUP_AND_INCIDENT_ALERTS.md** — Full behavior; section “Changes introduced on 20/02–22/02” updated.
- **docs/CHANGELOG_2026-02-20.md** — This file.

---

## Guard-before-write, success-path state update, diagnostics (latest)

- **Guard before write:** At the start of `ProcessAccidentStoryAsync`, idempotency is enforced using the per-case state table. The service calls `IsAccidentStoryAlreadyWrittenAsync(_caseAnnexStateRepo, tikCounter, ct)`. If it returns true, the method logs INFO `"ACCIDENTSTORY already written, skip | TikCounter=..., TikVisualID=..., NispahType=..., RunId=..., reason=already written"` and **returns without attempting any Odcanit write or dedup insert**. No call to the nispah writer occurs when state is already written.
- **Success path must set written state:** After a successful Odcanit annex write (`CreateNispahAsync` returns true), the service **always** calls `MarkAccidentStoryWrittenAsync(tikCounter, runId, ct)`. Explicit INFO logs were added: one **before** the state update (`"ACCIDENTSTORY state update: about to mark written | ..."`) and one **after** (`"ACCIDENTSTORY state update: marked written | ... reason=written ok"`) so runs can be proven from logs.
- **State update failure:** If `MarkAccidentStoryWrittenAsync` throws (e.g. DB failure), the service logs ERROR with `reason=state update failed`, calls `SendAlert` so the failure is not silent, and returns. This is not treated as an expected condition; repeated writes could occur until the state is successfully set.
- **Dedup-hit path:** Unchanged: on SQL 2601/2627 the handler still calls `MarkAccidentStoryWrittenAsync`, logs Warning with `reason=dedup hit`, and does not send a critical alert. ChangeTracker cleanup remains in NispahWriterService.
- **Diagnostic logging:** On every attempt or skip, logs now include **TikCounter**, **TikVisualID**, **NispahTypeName**, a short **InfoHash prefix** (first 8 chars of SHA256 hex of the note text, via `GetInfoHashPrefix(text)`), **RunId**, and **reason** (`already written` / `dedup hit` / `written ok` / `state update failed`). The WRITING log includes `InfoHashPrefix`; the "already written" skip does not have note text so no hash; dedup-hit log includes `reason=dedup hit`.
- **Helper for tests:** `IsAccidentStoryAlreadyWrittenAsync(ICaseAnnexWriteStateRepository repo, int tikCounter, CancellationToken ct)` is an internal static method that returns true when the state is already written. Used by the guard and by unit tests to prove that when state is written, the guard returns true (so the writer is not called).
- **Tests added:** (a) `IsAccidentStoryAlreadyWrittenAsync_WhenStateWritten_ReturnsTrue` — uses a fake repo that returns `AccidentStoryAnnexWritten=true`; asserts the helper returns true (so `ProcessAccidentStoryAsync` would skip and not call the writer). (b) `SuccessPath_MarkAccidentStoryWrittenAsync_CalledOnce_StatePersisted` — asserts that when `MarkAccidentStoryWrittenAsync` is called once, the state is persisted (Written=1, AtUtc, RunId set).
- **Files modified (this round):** `Services/DocumentIngestionService.cs` (guard using `IsAccidentStoryAlreadyWrittenAsync`, explicit before/after state-update logs, try/catch around `MarkAccidentStoryWrittenAsync`, `GetInfoHashPrefix`, diagnostic fields and reason in logs); `Odmon.Worker.Tests/AccidentStoryIdempotencyTests.cs` (fake `FakeStateRepoWrittenTrue`, tests `IsAccidentStoryAlreadyWrittenAsync_WhenStateWritten_ReturnsTrue` and `SuccessPath_MarkAccidentStoryWrittenAsync_CalledOnce_StatePersisted`).
