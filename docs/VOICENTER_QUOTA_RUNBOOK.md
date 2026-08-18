# Voicenter Quota & Backfill Runbook

> **Last updated:** May 2026  
> **Audience:** DevOps / Operators

This runbook covers Voicenter weekly-quota tracking, the warning flow, and the safe one-time backfill that recovers calls missed while the worker was blocked.

---

## 1. What changed (TL;DR)

- ODMON now records **every** outbound Voicenter API call in `dbo.VoicenterApiRequestLogs`, separated by `EndpointType`:
  - `CdrList` — `POST https://api.voicenter.com/hub/cdr/`
  - `CallHistoryDetail` — `GET https://api-manager.voicenter.co/api-manager-v1/Call/History/{CallID}`
  - `Other` — reserved for future endpoints
- The Voicenter weekly limit (currently **400 / week** for the configured account) is treated as applying **only** to `CallHistoryDetail` requests. `CdrList` is tracked separately for visibility.
- A **warning email** is sent once per ISO week when `CallHistoryDetail` usage reaches `WeeklyUsageWarningThreshold` (default 350).
- A typed `VoicenterQuotaExceededException` short-circuits the cycle the moment Voicenter returns HTTP 401 / a usage-limit body, and remaining CDR rows in the cycle are counted as `SkippedDueToQuotaExceeded`.
- A new local cache (`dbo.VoicenterCallProcessingStates`) stops the worker from re-fetching call details for CallIDs already known to be `Written`, `NoAI`, `NoMatch`, `Duplicate`, or `QuotaExceeded`.
- The daily summary email always explains why CDR rows did **not** turn into detail calls or written annexes.
- A `VoicenterBackfill` config block enables a safe, one-shot backfill across an arbitrary date range.

---

## 2. Quick simple explanation

Voicenter has a weekly cap on how many call-detail requests we may make. ODMON now counts every request, sends a heads-up email at 350/400, and remembers which calls it has already handled so it never wastes a quota slot on a call it already processed. After a quota outage, the backfill mode lets us catch up on missed calls without sending duplicates.

---

## 3. New IntegrationDb tables

| Table | Purpose |
|-------|---------|
| `dbo.VoicenterApiRequestLogs` | Audit row per outbound Voicenter request (endpoint type, HTTP status, quota flag, error). |
| `dbo.VoicenterQuotaWarningStates` | One row per (week, endpoint type) the moment a warning email is queued — prevents weekly warning spam. |
| `dbo.VoicenterCallProcessingStates` | Per-CallID cache: `Status` ∈ {`New`,`Written`,`NoAI`,`NoMatch`,`Duplicate`,`Failed`,`QuotaExceeded`}. Used to skip detail fetches for already-resolved CallIDs. |

Run the migration before the next worker start:

```powershell
dotnet ef database update --context IntegrationDbContext
```

---

## 4. Useful queries

### Weekly usage (current week)

```sql
DECLARE @WeekStart datetime2 = (
  SELECT DATEADD(day, -((DATEPART(weekday, SYSUTCDATETIME()) + 5) % 7),
                CAST(CAST(SYSUTCDATETIME() AS date) AS datetime2))
);

SELECT EndpointType,
       COUNT(*)                                    AS Requests,
       SUM(CASE WHEN Success = 1 THEN 1 ELSE 0 END) AS OkRequests,
       SUM(CASE WHEN QuotaExceeded = 1 THEN 1 ELSE 0 END) AS QuotaHits
FROM dbo.VoicenterApiRequestLogs
WHERE WeekStartUtc = @WeekStart
GROUP BY EndpointType
ORDER BY EndpointType;
```

### Was a quota warning email already sent this week?

```sql
SELECT TOP 5 * FROM dbo.VoicenterQuotaWarningStates ORDER BY WarningSentAtUtc DESC;
```

### Status of recently seen calls

```sql
SELECT TOP 100 CallId, Status, Attempts, LastSeenUtc, TikVisualId, LastError
FROM dbo.VoicenterCallProcessingStates
ORDER BY LastSeenUtc DESC;
```

---

## 5. Configuration (appsettings.json)

```jsonc
"VoicenterCallSummaries": {
  "Enabled": true,
  "IntervalHours": 12,
  "LookbackHours": 24,
  // … existing settings …
  "WeeklyUsageWarningThreshold": 350,   // CallHistoryDetail count that triggers the warning email
  "WeeklyUsageHardLimit": 400,          // Voicenter-enforced hard limit (informational in alert)
  "UsageWarningEmailEnabled": true
},
"VoicenterBackfill": {
  "Enable": false,            // master switch — turn on only for the backfill window
  "FromUtc": null,            // e.g. "2026-04-27T00:00:00Z"
  "ToUtc": null,              // null = DateTime.UtcNow
  "MaxCalls": 0,              // 0 = unlimited; set a safe cap during DryRun
  "ForceRecheck": false,      // true = retry calls previously NoAI/NoMatch/Failed
  "DryRun": true              // ALWAYS true on the first pass
}
```

> **Important:** Normal 24h lookback will not recover the full missed week automatically. A backfill is required for calls older than the current `LookbackHours` window.

---

## 6. Backfill procedure for the missed week (post-quota-reset)

### Step 0 — Pre-flight

1. Confirm Voicenter weekly quota has reset (e.g. with a single manual GET to `Call/History/{anyValidCallId}`).
2. Confirm IntegrationDb migration is applied (see §3).
3. Confirm no other ODMON worker is mid-cycle (check logs for `VOICENTER_WORKER`).

### Step 1 — DryRun (no Odcanit writes)

Edit `appsettings.json`:

```jsonc
"VoicenterBackfill": {
  "Enable": true,
  "FromUtc": "2026-04-27T00:00:00Z",
  "ToUtc": null,
  "MaxCalls": 50,
  "ForceRecheck": false,
  "DryRun": true
}
```

Restart the ODMON worker. Watch the log for:

```
VOICENTER BACKFILL | Range=2026-04-27T00:00:00.0000000Z..<now>, DryRun=True, MaxCalls=50, ForceRecheck=False
…
VOICENTER BACKFILL SUMMARY | CDR=…, Details=…, Written=…, Duplicate=…, NoAI=…, NoMatch=…, Failed=…, QuotaExceeded=…
```

`Written` in DryRun mode means "would write" — nothing is sent to Odcanit. Verify the numbers look plausible.

### Step 2 — Live run

Disable DryRun and lift / remove the cap:

```jsonc
"VoicenterBackfill": {
  "Enable": true,
  "FromUtc": "2026-04-27T00:00:00Z",
  "ToUtc": null,
  "MaxCalls": 0,
  "ForceRecheck": false,
  "DryRun": false
}
```

Restart the worker. Verify in `dbo.NispahWriteLogs` that new rows with `SourceKind='VoicenterCall'` appear.

### Step 3 — Disable backfill

After the catch-up cycle completes:

```jsonc
"VoicenterBackfill": { "Enable": false, ... }
```

Restart the worker so it returns to the normal `LookbackHours` schedule.

---

## 7. Safety guarantees

- **No duplicate annexes:** `NispahWriteLogs` (`SourceKind+SourceItemId+TikCounter`) is checked **before** every write, both in normal mode and in backfill mode. `ForceRecheck` does not bypass it for already-`Written` CallIDs.
- **No Odcanit schema changes:** all new tables live in IntegrationDb only.
- **No Monday board changes:** Monday columns/labels are untouched.
- **Quota safety in backfill:** the moment a quota response is detected (HTTP 401 or body containing "weekly usage limit" / "usage limit" / "quota" / "limit reached"), the cycle stops issuing further `CallHistoryDetail` requests.

---

## 8. Daily summary changes

The "Voicenter Call Summaries" section now always shows:

- CDR fetched, details fetched, written, no-AI, no-match, duplicate counts (last run)
- A breakdown of **why CDR rows did not become detail requests**: missing CallID, duplicate-before-detail, already-processed, quota-exceeded, pre-detail filter, detail fetch failed
- Weekly Voicenter API usage, separated by endpoint, with the warning threshold and hard limit
- A red banner if any quota-exceeded response was recorded in the last 24h

If a run produces `Fetched > 0, Details = 0, Written = 0` and **no** explanatory counter is non-zero, the summary now flags it explicitly as a bug-worthy condition rather than silently appearing healthy.

---

## 9. Acceptance check after deployment

| Check | How |
|-------|-----|
| Migration applied | `SELECT TOP 1 * FROM dbo.VoicenterApiRequestLogs;` returns without error |
| Per-request logging works | After one cycle: row count > 0 in `VoicenterApiRequestLogs` |
| Endpoint separation works | `SELECT EndpointType, COUNT(*) ... GROUP BY EndpointType` shows both `CdrList` and `CallHistoryDetail` |
| Quota detection works | After a 401 response: row with `QuotaExceeded=1` appears |
| Warning email throttled | Only one row per (week, endpoint) in `VoicenterQuotaWarningStates` per week |
| Dedup works | Re-running the worker does not increase `CallHistoryDetail` request count for already-Written CallIDs |
| Daily summary explains zero-details | Section "Why CDR rows did not become detail requests" populated |
