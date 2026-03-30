# MondayItemMappings Lookup — NOLOCK Strategy (2026-03-23)

## Problem

The SyncWorker consistently crashed at bootstrap onboarding with a 30-second
`CommandTimeout` on the MondayItemMappings lookup query.

The table had only ~431 rows.  Indexes were present and correct.  The same
query succeeded immediately under `READ UNCOMMITTED`.

**Root cause:** The sync process itself writes to `MondayItemMappings` (INSERT
on bootstrap create, UPDATE on reconcile).  Under the default `READ COMMITTED`
isolation, the bulk-read query was blocked behind row/page locks held by those
writes, exceeding `CommandTimeout` before the locks were released.

This is lock-wait contention, not a missing-index or data-size problem.

## Fix

### Primary: raw SQL with `WITH (NOLOCK)`

`SyncService.QueryMappedTikCountersNolockAsync` executes a parameterized
raw SQL query that reads only the `TikCounter` column:

```sql
SELECT DISTINCT m.TikCounter
FROM dbo.MondayItemMappings m WITH (NOLOCK)
WHERE m.BoardId = @boardId
  AND m.TikCounter IN (@tc0, @tc1, …)
```

The query is **candidate-scoped**: it only asks about the TikCounters the
current sync run actually needs, not the entire board.  This replaces the
previous board-wide `DISTINCT` preload.

### Why NOLOCK is safe here

This lookup is used solely to *partition* candidates into "already-mapped"
and "unmapped" sets.  Both downstream paths have per-case safety:

| Path | Guard |
|------|-------|
| Bootstrap create | `FirstOrDefaultAsync(TikCounter + BoardId)` under normal isolation before INSERT |
| Reconcile update | Mapping looked up individually per case before UPDATE |

A dirty read here can only produce two benign outcomes:

1. **Phantom mapped row** (uncommitted INSERT) — case skipped as "already
   mapped".  Correct: the item is indeed being created.
2. **Missed mapping** (uncommitted row not visible) — case treated as
   unmapped.  The per-case race check catches it before creating a duplicate.

Additionally, a `UNIQUE INDEX` on `TikCounter` in `MondayItemMappings`
enforces uniqueness at the database level as a final safety net.

No business-rule change.  No duplicate Monday items.

### Secondary: batching + progressive fallback

`SyncService.LoadMappedTikCountersForCandidatesAsync` wraps the raw SQL call
with resilience:

- **Batch size**: 100 candidates per query (well within SQL Server's 2100-
  parameter limit)
- **Progressive retry**: on any failure, halve batch size (100 → 50 → 25 → …
  → 1) and retry only the failed items
- **Graceful degradation**: if per-case lookups still fail, those TikCounters
  are treated as unmapped (safe for the reasons above)
- **Never crashes the worker**: only `OperationCanceledException` propagates

### Alert classification

`SqlConnectionFailureDetector` separates:

| SQL error | Classification |
|-----------|---------------|
| -1, -2    | `IntegrationDb Query Timeout` |
| 2, 20, 53, 64, 233, 10053, 10054, 10060 | `Database Connection Lost` |

`IsConnectionFailure()` still returns true for both (backward-compatible).
Workers now send the correct `alertType` string to email notifications.

## Scope

- NOLOCK is applied **only** to `QueryMappedTikCountersNolockAsync`.  No
  global isolation-level change.  No other table or query is affected.
- Call sites: `RunBootstrapOnboardingAsync` and the reconciliation phase in
  `SyncOdcanitToMondayAsync`, both via `LoadMappedTikCountersForCandidatesAsync`.

## Files changed

| File | What |
|------|------|
| `Services/SyncService.cs` | Added `QueryMappedTikCountersNolockAsync`; updated `LoadMappedTikCountersForCandidatesAsync` to call it |
| `Services/SyncService_Bootstrap.cs` | Uses `LoadMappedTikCountersForCandidatesAsync` (candidate-scoped, not board-wide) |
| `Services/SqlConnectionFailureDetector.cs` | Split `IsQueryTimeout` / `IsNetworkFailure` |
| `Workers/SyncWorker.cs` | Accurate alert classification |
| `Workers/DocumentIngestionWorker.cs` | Accurate alert classification |
