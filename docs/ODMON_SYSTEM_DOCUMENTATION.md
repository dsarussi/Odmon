# ODMON System Documentation

> **Last updated:** April 2026  
> **Audience:** Developers, DevOps, Operators, Stakeholders

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Architecture](#2-architecture)
3. [Workers and Responsibilities](#3-workers-and-responsibilities)
4. [Feature Flows](#4-feature-flows)
   - 4.1 [Odcanit → Monday Sync](#41-odcanit--monday-sync)
   - 4.2 [Hearing Nearest Sync](#42-hearing-nearest-sync)
   - 4.3 [Hearing Approval → Annex Writing](#43-hearing-approval--annex-writing)
   - 4.4 [Hearing Approval Backfill](#44-hearing-approval-backfill)
   - 4.5 [Hearing Backfill (Bulk Import)](#45-hearing-backfill-bulk-import)
   - 4.6 [Document Ingestion (Questionnaire Board)](#46-document-ingestion-questionnaire-board)
   - 4.7 [Document Ingestion (Tasks Board)](#47-document-ingestion-tasks-board)
   - 4.8 [Accident Story Annex Writing](#48-accident-story-annex-writing)
   - 4.9 [Voicenter Call Summaries → Annex Writing](#49-voicenter-call-summaries--annex-writing)
5. [Integrations](#5-integrations)
   - 5.1 [Monday.com](#51-mondaycom)
   - 5.2 [Odcanit (Legal Case Management)](#52-odcanit-legal-case-management)
   - 5.3 [Voicenter (Call Center / AI Summaries)](#53-voicenter-call-center--ai-summaries)
6. [Data Storage and Logging](#6-data-storage-and-logging)
   - 6.1 [Integration Database](#61-integration-database)
   - 6.2 [Odcanit Database](#62-odcanit-database)
   - 6.3 [Deduplication Mechanisms](#63-deduplication-mechanisms)
   - 6.4 [Audit and Write Logs](#64-audit-and-write-logs)
7. [Error Handling and Resilience](#7-error-handling-and-resilience)
8. [Daily Summary Email](#8-daily-summary-email)
9. [Alerting and Monitoring](#9-alerting-and-monitoring)
10. [Configuration and Deployment](#10-configuration-and-deployment)
11. [Secret Management](#11-secret-management)
12. [Testing Modes](#12-testing-modes)

---

## 1. System Overview

**What it does in simple terms:**  
ODMON is an automated bridge between a law firm's case management system (Odcanit) and project management boards (Monday.com). It keeps case data synchronized, ingests documents from online forms, writes court-related notes back to Odcanit, and pulls AI-generated phone call summaries to attach to case files.

**Technical summary:**  
ODMON is a .NET 8 worker service that runs as a Windows Service. It hosts multiple background workers that coordinate data flow between Odcanit (on-premises SQL Server legal case management), Monday.com (SaaS project management boards), and Voicenter (cloud telephony with AI call summarization). The system uses Entity Framework Core for data access, periodic polling for sync, HTTP/GraphQL for Monday and Voicenter APIs, and stored procedures for Odcanit writes.

**Core capabilities:**

| Capability | Direction | Description |
|-----------|-----------|-------------|
| Case sync | Odcanit → Monday | Replicate case data as Monday items |
| Hearing sync | Odcanit → Monday | Track nearest upcoming court hearing per case |
| Hearing approval | Monday → Odcanit | Write hearing attendance confirmation as annex |
| Document ingestion | Monday → Odcanit | Download files from Monday, import into Odcanit |
| Accident story | Monday → Odcanit | Compose questionnaire answers into annex text |
| Call summaries | Voicenter → Odcanit | Attach AI call summaries as case annexes |
| Monitoring | Internal | Daily summary email, critical alerts, failure tracking |

---

## 2. Architecture

### Component Layout

```
┌──────────────────────────────────────────────────────┐
│                    ODMON Worker Service               │
│                                                      │
│  ┌──────────────┐  ┌─────────────────────────┐       │
│  │  SyncWorker   │  │ DocumentIngestionWorker │       │
│  └──────┬───────┘  └───────────┬─────────────┘       │
│         │                      │                      │
│         │   ┌──────────────────┤                      │
│         │   │  WorkerCoordinator (SemaphoreSlim)      │
│         │   │  (prevents concurrent DB-heavy work)    │
│         │   └──────────────────┘                      │
│         │                                             │
│  ┌──────┴──────────┐  ┌──────────────────────┐       │
│  │   SyncService    │  │DocumentIngestionSvc  │       │
│  │  (partial class) │  └──────────────────────┘       │
│  └─────────────────┘                                  │
│                                                      │
│  ┌──────────────────────┐  ┌─────────────────────┐   │
│  │VoicenterCallSummary  │  │ EmailBackground     │   │
│  │     Worker           │  │    Service           │   │
│  └──────────┬───────────┘  └──────────┬──────────┘   │
│             │                         │               │
│  ┌──────────┴───────────┐  ┌──────────┴──────────┐   │
│  │VoicenterCallSummary  │  │  EmailNotifier       │   │
│  │     Service          │  │  (SMTP queue)        │   │
│  └──────────────────────┘  └─────────────────────┘   │
│                                                      │
│  ┌──────────────────────┐  ┌─────────────────────┐   │
│  │HearingBackfillWorker │  │HearingApproval      │   │
│  │                      │  │  BackfillWorker      │   │
│  └──────────────────────┘  └─────────────────────┘   │
└──────────────────────────────────────────────────────┘
           │              │               │
     ┌─────┴─────┐  ┌────┴────┐   ┌──────┴──────┐
     │ Odcanit   │  │Monday   │   │ Voicenter   │
     │ SQL Server│  │.com API │   │ API         │
     └───────────┘  └─────────┘   └─────────────┘
```

### Worker Coordination

**What it does in simple terms:**  
A traffic light ensures only one heavy worker runs at a time, so the database doesn't get overloaded.

**Technical detail:**  
`WorkerCoordinator` is a singleton that wraps a `SemaphoreSlim(1,1)`. The `SyncWorker` and `DocumentIngestionWorker` compete for a lease before running their cycles. If the lease is held, the other worker skips its cycle gracefully. The `EmailBackgroundService` and `VoicenterCallSummaryWorker` are not gated by this coordinator — email must always be sendable, and Voicenter primarily hits external APIs rather than the integration database.

### Dependency Injection

All workers, services, and infrastructure are registered in `Program.cs`. Key registrations:
- **Singletons:** `WorkerCoordinator`, `VoicenterApiClient`, `EmailNotifier`
- **Scoped (per-cycle):** `SyncService`, `VoicenterCallSummaryService`, `DocumentIngestionService`, `HearingApprovalSyncService`, `HearingNearestSyncService`, `NispahWriterService`
- **Hosted services:** `SyncWorker`, `DocumentIngestionWorker`, `EmailBackgroundService`, `VoicenterCallSummaryWorker`, `HearingBackfillWorker`, `HearingApprovalBackfillWorker`
- **DbContexts:** `IntegrationDbContext`, `OdcanitDbContext` (both scoped, SQL Server)

---

## 3. Workers and Responsibilities

### SyncWorker

**What it does:** Periodically reads case data from Odcanit and creates or updates matching items on Monday.com.

| Property | Value |
|---------|-------|
| Class | `SyncWorker` |
| Interval | Configurable via `Sync:IntervalSeconds` (default 180s) |
| Coordination | Acquires `WorkerCoordinator` lease |
| Delegates to | `SyncService` |
| Config section | `Sync`, `Monday`, `OdcanitLoad`, `Testing`, `Safety` |
| Key behavior | Startup diagnostics, heartbeat logging, SQL failure alerts |

### DocumentIngestionWorker

**What it does:** Downloads files from Monday questionnaire and tasks boards, then imports them into Odcanit's document management system.

| Property | Value |
|---------|-------|
| Class | `DocumentIngestionWorker` |
| Interval | Configurable via `MondayDocumentIngestion:IntervalSeconds` (default 300s) |
| Coordination | Acquires `WorkerCoordinator` lease |
| Delegates to | `DocumentIngestionService` |
| Config section | `MondayDocumentIngestion`, `OdcanitDocuments` |
| Key behavior | Checks `NispahDeduplications` table health on startup |

### EmailBackgroundService

**What it does:** Processes queued emails (alerts, digests) and sends a daily HTML summary of system activity.

| Property | Value |
|---------|-------|
| Class | `EmailBackgroundService` |
| Interval | Continuous queue processing + periodic tasks |
| Coordination | None (never gated) |
| Config section | `Email` |
| Key behavior | Daily summary at configurable Israel time, periodic digest, SMTP via Office365 |

### VoicenterCallSummaryWorker

**What it does:** Fetches phone call records from Voicenter, retrieves AI-generated summaries, and writes them as annexes to matching Odcanit cases.

| Property | Value |
|---------|-------|
| Class | `VoicenterCallSummaryWorker` |
| Interval | Configurable via `VoicenterCallSummaries:IntervalHours` (default 12h) |
| Coordination | None (primarily external API calls) |
| Delegates to | `VoicenterCallSummaryService` |
| Config section | `VoicenterCallSummaries` |
| Key behavior | Per-call isolation, stale worker detection, unhandled exception alerts, alert throttling |

### HearingBackfillWorker

**What it does:** One-time batch import of hearing data from a staging SQL table into Monday items.

| Property | Value |
|---------|-------|
| Class | `HearingBackfillWorker` |
| Behavior | Runs batches until source table is empty, then exits |
| Config section | `HearingBackfill` |
| Typical use | Disabled in production; enabled for migration events |

### HearingApprovalBackfillWorker

**What it does:** One-time historical backfill that writes hearing approval annexes for all existing approved/rejected items.

| Property | Value |
|---------|-------|
| Class | `HearingApprovalBackfillWorker` |
| Behavior | Single pass, then exits |
| Config section | `HearingApprovalBackfill` |
| Typical use | Disabled; enabled after feature launch to cover historical data |

---

## 4. Feature Flows

### 4.1 Odcanit → Monday Sync

**What it does in simple terms:**  
Case information from the law firm's system appears automatically on Monday.com boards, keeping everyone up to date without manual data entry.

**Technical flow:**

1. **TikCounter selection** — `SyncService` determines which cases to load. Two modes:
   - *Allowlist mode* (`OdcanitLoad:EnableAllowList`): load only specified TikCounters/TikNumbers
   - *Change feed mode*: query `vwExportToOuterSystems_ActionLog` for cases modified since last watermark
2. **Case loading** — `IOdcanitReader` (`SqlOdcanitReader`) loads full case data from Odcanit views, enriching each `OdcanitCase` with clients, sides, diary events, user data, and hozlap data
3. **Bootstrap onboarding** — New cases (no existing `MondayItemMapping`) undergo a cooling period (`Onboarding:CoolingPeriodDays`) before being created as Monday items. This prevents creating items for cases that are immediately closed or corrected
4. **Reconciliation** — Existing mapped cases are compared via checksums (`OdcanitVersion`, `MondayChecksum`). Only changed fields trigger a Monday API update
5. **Hearing sync** — Delegated to `HearingNearestSyncService` (see 4.2)
6. **Hearing approval sync** — Delegated to `HearingApprovalSyncService` (see 4.3)
7. **Circuit breaker** — If failures exceed `Monday:CircuitBreakerFailureThreshold`, the run is aborted and a metric is recorded
8. **Run lock** — `SyncRunLock` table prevents overlapping runs (single-row lock with expiry)
9. **Metrics** — Every run logs a `SyncRunMetric` row: created, updated, failed, skipped counts, duration, circuit breaker status

**Key tables:** `MondayItemMappings`, `SyncLogs`, `SyncFailures`, `SyncRunMetrics`, `SyncRunLocks`, `ListenerStates`

### 4.2 Hearing Nearest Sync

**What it does in simple terms:**  
For each case, the system finds the next upcoming court hearing and shows its details (date, judge, city) on the Monday board.

**Technical flow:**

1. `HearingNearestSyncService.SyncNearestHearingsAsync` is called during each sync cycle
2. `HearingSelector.PickNearestUpcomingHearing` selects the closest future diary event per `TikCounter` from `OdcanitDiaryEvent` rows
3. Changes are detected by comparing against `HearingNearestSnapshots`
4. Monday columns updated: hearing date, hour, judge name, court city, hearing status
5. Snapshots are persisted for next-run comparison

**Key tables:** `HearingNearestSnapshots`, `MondayItemMappings`

### 4.3 Hearing Approval → Annex Writing

**What it does in simple terms:**  
When a client confirms or declines their court appearance on the Monday board, the system automatically records this decision in the Odcanit case file.

**Technical flow:**

1. `HearingApprovalSyncService.SyncAsync` runs within each sync cycle
2. Reads Monday column `color_mkzbmv1b` ("אישור הגעה לדיון") for all mapped items
3. Compares against `MondayHearingApprovalStates` to detect transitions
4. Only actionable transitions (→ approved index `1`, → rejected index `2`) trigger a write
5. Calls `IOdcanitWriter.AppendNispahAsync` with stored procedure `dbo.Klita_Interface_NispahDetails`
6. Annex text: "אישר הגעה לדיון" (approved) or "לא אישר הגעה לדיון" (rejected)
7. Writes a `NispahWriteLog` entry (`SourceKind="HearingApproval"`) for audit
8. Updates `MondayHearingApprovalState` to prevent duplicate writes
9. **DryRun mode**: When `OdcanitWrites:DryRun` is true, logs only — no state changes, no Odcanit writes

**Key tables:** `MondayHearingApprovalStates`, `NispahWriteLogs`

### 4.4 Hearing Approval Backfill

**What it does in simple terms:**  
A one-time job that goes back through all existing approvals/rejections and writes them to Odcanit, for cases that were approved before the live feature was turned on.

**Technical flow:**

1. `HearingApprovalBackfillService.RunAsync` iterates all `MondayItemMappings`
2. For each mapping, fetches Monday hearing approval column value
3. Skips if a `NispahWriteLog` with `SourceKind="HearingApproval"` already exists for this TikCounter (proof of prior write, not just state tracking)
4. For `TikCounter <= 0` (negative mappings from import), resolves real TikCounter from Odcanit `dbo.MainTik` using TikNumber
5. Writes annex via `IOdcanitWriter.AppendNispahAsync` and records `NispahWriteLog`
6. Supports `DryRun`, `MaxItems`, `OnlyTikCounters` filters, and `ThrottleMs` pacing

**Key tables:** `MondayItemMappings`, `NispahWriteLogs`, `MondayHearingApprovalStates`

### 4.5 Hearing Backfill (Bulk Import)

**What it does in simple terms:**  
Imports a batch of hearing records from a staging table into Monday.com, used during data migration events.

**Technical flow:**

1. `HearingBackfillService.RunAsync` reads rows from a configurable source table (e.g. `dbo.HearingBackfill_May2026`)
2. Handles type mismatches: `ClientNumber` as `string` (from `NVARCHAR`), `HearingTime` as `string` (from `TimeSpan`)
3. Builds Monday column values and creates items via the Monday API
4. Marks imported rows with a status column update
5. Creates `MondayItemMapping` entries for new items

**Key tables:** Configurable source table (Hebrew columns), `MondayItemMappings`

### 4.6 Document Ingestion (Questionnaire Board)

**What it does in simple terms:**  
Files uploaded to the Monday questionnaire form (photos, PDFs, videos) are automatically imported into the Odcanit case file, so attorneys have all documents in one place.

**Technical flow:**

1. `DocumentIngestionService.RunIngestionAsync` fetches items from the Monday questionnaire board
2. For each item, finds the linked case via a relation column → TikNumber lookup
3. For each configured file column (`file_mm0qwtat`, `file_mkzr2cmr`, `file_mkyet713`), downloads assets from Monday
4. **Validation pipeline** per asset:
   - Denylist check: dangerous extensions (`exe`, `msi`, `bat`, etc.) are blocked → `DENYLIST_EXTENSION`
   - Size check: per-column limit (e.g. `file_mkyet713` = 35 MiB) and global limit (50 MiB) → `FILE_TOO_LARGE`
   - Extension validation: MIME sniffing + magic byte detection for ambiguous files
   - HEIC/HEIF vs MP4/MOV: ISO BMFF `ftyp` major brand differentiation
5. File is saved to inbox directory, then Odcanit stored procedure `dbo.ProcDocuments_AddNewDocument` creates the document record
6. File is moved to final Odcanit document path
7. Tracking in `MondayDocumentImports` with states: `Pending → InProgress → Success/Failed/Skipped`
8. Retries up to `MaxRetryCount` for transient failures

**Supported file types:** `pdf`, `jpg`, `jpeg`, `png`, `docx`, `doc`, `mp4`, `mov`, `qt`, `heic`, `heif`

**Denied file types:** `exe`, `msi`, `bat`, `cmd`, `ps1`, `js`, `vbs`, `scr`, `com`, `hta`, `jar`, `zip`, `rar`, `7z`

**Key tables:** `MondayDocumentImports`

### 4.7 Document Ingestion (Tasks Board)

**What it does in simple terms:**  
PDF and Word documents uploaded to a separate Monday "tasks" board are also automatically imported into Odcanit.

**Technical flow:**

1. Same `DocumentIngestionService.RunIngestionAsync` method handles this when `TasksSource:Enabled` is true
2. Fetches items from a different board (`TasksSource:BoardId`)
3. Resolves TikNumber via a lookup column
4. Downloads file from the tasks board file column
5. On success, updates a Monday status column to indicate the form was processed

**Config:** Nested under `MondayDocumentIngestion:TasksSource`

### 4.8 Accident Story Annex Writing

**What it does in simple terms:**  
Answers from the online accident questionnaire (what happened, were there witnesses, etc.) are compiled into a summary and written as a note in the Odcanit case file.

**Technical flow:**

1. During document ingestion, `ProcessAccidentStoryAsync` runs for each questionnaire item
2. `AccidentStoryComposer.Compose` collects values from configured Monday columns (long text, status fields)
3. Composes a formatted Hebrew text block with question-answer pairs
4. Writes via `NispahWriterService.CreateNispahAsync` (SP `dbo.Klita_Interface_NispahDetails`)
5. Idempotency: `CaseAnnexWriteState.AccidentStoryAnnexWritten` flag per TikCounter prevents duplicate writes
6. Additional dedup via `NispahDeduplications` table (content hash)

**Key tables:** `CaseAnnexWriteStates`, `NispahDeduplications`, `NispahAuditLogs`

### 4.9 Voicenter Call Summaries → Annex Writing

**What it does in simple terms:**  
When a phone call is made to a client or witness and the AI generates a summary of the conversation, that summary is automatically attached to the relevant Odcanit case file.

**Technical flow:**

1. `VoicenterCallSummaryWorker` runs on a 12-hour cycle (configurable)
2. Fetches CDR (Call Detail Records) from Voicenter's `/hub/cdr/` API with body code authentication
3. Filters: only answered calls (configurable), minimum duration, lookback window
4. For each qualifying CDR entry, fetches full call details from `/Call/History/{CallID}` with Bearer token
5. Extracts AI summary from `Data.ai_data.insights.summary`
6. Resolves client phone from `Data.ai_data.client_phone` → `Data.cdr_data.client_phone` → target/caller fallbacks
7. Normalizes to Israeli phone format (deterministic `0XX-XXXXXXX` style)
8. Matches against Odcanit cases by querying `vwExportToOuterSystems_UserData` for "סלולרי עד" (witness mobile) and "נייד צד ג" (third-party driver mobile) fields
9. **Multi-match rule**: writes to ALL matched cases (not just first)
10. Dedup per `CallID + TikCounter` via `NispahWriteLogs` (`SourceKind="VoicenterCall"`, `SourceItemId` = hashed CallID)
11. Writes annex via `IOdcanitWriter.AppendNispahAsync`
12. Annex format: date, time, duration, status (Hebrew), and AI summary text

**Per-call isolation:** Each CallID is processed in its own try/catch. A malformed payload for one call does not fail the entire cycle.

**Stale worker detection:** If no successful cycle occurs within `StaleWorkerThresholdHours` (default 18h), an alert email is sent.

**Test mode:** When `TestMode=true` and `TestCallId` is set, only that single call is processed (with diagnostic logging).

**Weekly quota tracking (May 2026):** Voicenter enforces a weekly usage limit (currently 400/week for User 203570) on the `Call/History/{CallID}` endpoint. ODMON now records every API request in `VoicenterApiRequestLogs` separated by `EndpointType` (`CdrList` vs `CallHistoryDetail`), counts the current ISO week, and queues a warning email once per week when usage reaches `WeeklyUsageWarningThreshold` (default 350). When Voicenter returns HTTP 401 or a body containing "weekly usage limit" / "usage limit" / "quota" / "limit reached", `VoicenterApiClient` throws `VoicenterQuotaExceededException`; the service stops issuing further detail requests for the cycle and counts remaining CDR rows as `SkippedDueToQuotaExceeded`.

**Local processing-state cache:** `VoicenterCallProcessingStates` stores per-CallID terminal status (`Written`, `NoAI`, `NoMatch`, `Duplicate`, `Failed`, `QuotaExceeded`). The worker checks this cache **before** issuing a `CallHistoryDetail` request, so already-resolved CallIDs do not consume quota. `NispahWriteLogs` is also checked as a second proof of prior write.

**Manual backfill mode:** `VoicenterBackfill` config block enables a one-shot wider date range (e.g. recovering all calls since 2026-04-27 after a quota outage). Defaults to `DryRun=true` for safe preview. See `docs/VOICENTER_QUOTA_RUNBOOK.md`.

**Key tables:** `NispahWriteLogs`, `VoicenterApiRequestLogs`, `VoicenterQuotaWarningStates`, `VoicenterCallProcessingStates`

---

## 5. Integrations

### 5.1 Monday.com

**What it does in simple terms:**  
Monday.com is the project board where the law firm tracks cases. ODMON reads from and writes to it.

| Component | Purpose |
|-----------|---------|
| `IMondayClient` / `MondayClient` | GraphQL mutations (create item, change column values) |
| `IMondayMetadataProvider` / `MondayMetadataProvider` | Board metadata, column definitions, allowed label resolution with caching |
| `DocumentIngestionMondayService` | GraphQL queries for questionnaire/task board items, asset download URLs, column values |

**Authentication:** API token resolved via `ISecretProvider` (key: `Monday__ApiToken`).

**Board structure:**
- **Cases board** (`Monday:BoardId` / `CasesBoardId`): Main case tracking, 90+ columns mapped from Odcanit
- **Questionnaire board** (`MondayDocumentIngestion:BoardId`): Client intake forms with file uploads
- **Tasks board** (`MondayDocumentIngestion:TasksSource:BoardId`): Attorney task documents

**Column mapping:** Defined in `Monday:*ColumnId` settings. Full mapping documented in `docs/ODMON_Monday_Mapping.md`.

### 5.2 Odcanit (Legal Case Management)

**What it does in simple terms:**  
Odcanit is the law firm's main database. ODMON reads case data from it and writes documents and notes back to it.

**Read access (views):**

| View | Entity | Used for |
|------|--------|----------|
| `vwExportToOuterSystems_Files` | `OdcanitCase` | Case master data |
| `vwExportToOuterSystems_LoginUsers` | `OdcanitUser` | User lookup |
| `vwExportToOuterSystems_Clients` | `OdcanitClient` | Client details |
| `vwExportToOuterSystems_vwSides` | `OdcanitSide` | Case parties |
| `vwExportToOuterSystems_YomanData` | `OdcanitDiaryEvent` | Court calendar/diary |
| `vwExportToOuterSystems_UserData` | `OdcanitUserData` | Custom field values |
| `vwHozlapFormsData_TikMainData` | `OdcanitHozlapMainData` | Court case numbers |
| `vwExportToOuterSystems_ActionLog` | (raw SQL) | Change feed detection |

**Write access (stored procedures):**

| Stored Procedure | Used by | Purpose |
|-----------------|---------|---------|
| `dbo.Klita_Interface_NispahDetails` | `SqlOdcanitWriter`, `NispahWriterService` | Write annex/note to case file |
| `dbo.ProcDocuments_AddNewDocument` | `OdcanitDocumentWriter` | Create document record (file import) |

**Connection:** SQL Server, connection string resolved via `ISecretProvider` (key: `OdcanitDb__ConnectionString`).

### 5.3 Voicenter (Call Center / AI Summaries)

**What it does in simple terms:**  
Voicenter is the phone system. It records calls and creates AI summaries. ODMON pulls these summaries and attaches them to case files.

**API endpoints:**

| Endpoint | Auth | Purpose |
|----------|------|---------|
| `POST https://api.voicenter.com/hub/cdr/` | Body parameter `code` | Fetch CDR list |
| `GET https://api-manager.voicenter.co/api-manager-v1/Call/History/{callId}` | Bearer token header | Fetch call detail + AI summary |

**CDR response shape:** Root object with `CDR_LIST` property containing an array of call records.

**Call detail key paths:**
- AI summary: `Data.ai_data.insights.summary`
- Client phone: `Data.ai_data.client_phone` (primary), `Data.cdr_data.client_phone` (fallback)
- Status: `Data.cdr_data.DialStatus`
- Duration: `Data.cdr_data.Duration`

**Defensive JSON parsing:** All `JsonElement` access is guarded by `ValueKind` checks. Malformed payloads are skipped with a warning, never crashing the worker.

---

## 6. Data Storage and Logging

### 6.1 Integration Database

**What it does in simple terms:**  
The Integration Database is ODMON's own working memory — it tracks what has been synced, what documents are being processed, and what has been written to Odcanit.

**Entity overview:**

| Table | Purpose |
|-------|---------|
| `MondayItemMappings` | Links Odcanit `TikCounter` to Monday `ItemId` + `BoardId` |
| `SyncLogs` | Operational sync log entries |
| `SyncFailures` | Dead-letter table for failed sync operations |
| `SyncRunMetrics` | Per-run aggregate metrics |
| `SyncRunLocks` | Single-row distributed lock to prevent overlapping runs |
| `ListenerStates` | Change feed watermark (last processed timestamp) |
| `MondayHearingApprovalStates` | Tracks last known hearing approval status per Monday item |
| `HearingNearestSnapshots` | Cached nearest-hearing data per case for change detection |
| `MondayDocumentImports` | File ingestion pipeline tracking (per asset lifecycle) |
| `NispahWriteLogs` | Durable audit of all nispah (annex) writes to Odcanit |
| `NispahAuditLogs` | General nispah operation audit trail |
| `NispahDeduplications` | Content-hash dedup for nispah writes |
| `CaseAnnexWriteStates` | Per-case idempotency flags (e.g. accident story written) |
| `AllowedTiks` | Allowlist of TikCounters for controlled loading |
| `EmailAlertDedups` | Email alert deduplication and rate limiting |
| `VoicenterApiRequestLogs` | Audit row per outbound Voicenter API request, separated by endpoint type (for quota tracking) |
| `VoicenterQuotaWarningStates` | One row per (week, endpoint type) when a weekly quota warning email is queued — prevents weekly warning spam |
| `VoicenterCallProcessingStates` | Per-CallID terminal status cache; stops the worker from re-fetching details for already-resolved calls |

**Indexes on `MondayItemMappings`:** Unique indexes on `TikCounter`, `(TikNumber, BoardId)`, and `MondayItemId` for efficient lookups and constraint enforcement.

### 6.2 Odcanit Database

Read-only access via EF Core views (see Section 5.2). Write access only via stored procedures.

### 6.3 Deduplication Mechanisms

ODMON uses multiple layers of deduplication to prevent duplicate writes:

| Mechanism | Scope | How it works |
|-----------|-------|--------------|
| `NispahWriteLogs` | All nispah writes (hearing approval, Voicenter calls) | Query by `SourceKind + SourceItemId + TikCounter + !Failed` before writing |
| `NispahDeduplications` | Accident story and general nispah | Content hash (`TikVisualID + NispahTypeName + InfoHash`) with time window |
| `MondayDocumentImports` | Document ingestion | Track per `(QuestionnaireItemId, ColumnId, AssetId)` with status lifecycle |
| `CaseAnnexWriteStates` | Accident story per case | Boolean flag `AccidentStoryAnnexWritten` per `TikCounter` |
| `MondayHearingApprovalStates` | Hearing approval live sync | `LastKnownStatus` prevents re-processing same status |
| `SyncRunLocks` | Sync runs | Single-row lock with expiry prevents overlapping sync cycles |
| `MondayItemMappings` checksum | Case field sync | `OdcanitVersion` / `MondayChecksum` prevent no-op Monday API calls |

### 6.4 Audit and Write Logs

**`NispahWriteLogs`** is the primary audit table for all Odcanit write-back operations:

| Column | Purpose |
|--------|---------|
| `SourceKind` | Discriminator: `"HearingApproval"`, `"VoicenterCall"` |
| `SourceItemId` | Origin identifier (Monday item ID or hashed CallID) |
| `TikCounter` | Odcanit case |
| `Failed` | Whether the write succeeded or failed |
| `ErrorMessage` | Error details on failure, CallID reference on success |
| `InfoHash` | SHA-256 hash of the annex content |
| `CreatedAtUtc` | Timestamp |

This table serves dual purposes: **deduplication** (skip if already written) and **audit** (searchable proof of every write).

---

## 7. Error Handling and Resilience

### Per-Call / Per-Item Isolation

**What it does in simple terms:**  
If one item fails to process, the system skips it and moves on to the next. One bad apple doesn't spoil the batch.

**Implementation across features:**

| Feature | Isolation mechanism |
|---------|-------------------|
| Voicenter call processing | Each CallID wrapped in try/catch; failure increments counter, logs error, continues |
| Document ingestion | Each asset processed independently; failures logged to `MondayDocumentImports` with retry |
| Sync reconciliation | Per-case try/catch; failures recorded in `SyncFailures`; circuit breaker stops run if too many |
| Hearing approval | Per-item processing; exceptions logged, state not advanced on failure |

### Circuit Breaker (Sync)

If consecutive failures during a sync run exceed `Monday:CircuitBreakerFailureThreshold` (default 10), the run aborts early and records `CircuitBreakerTripped=true` in `SyncRunMetrics`.

### Retry Logic

- **Document ingestion**: Up to `MaxRetryCount` (default 3) retries with exponential backoff
- **Monday API calls**: `MaxRetryAttempts` (default 3) for transient GraphQL failures
- **Voicenter**: No automatic retry per call; next cycle will re-process if not yet written (dedup check)

### SQL Connection Failure Detection

`SqlConnectionFailureDetector` classifies SQL errors into:
- Query timeouts
- Network failures
- Connection failures

This drives alert escalation decisions.

### Crash Safety

- **Document ingestion**: Checkpoint after `CreateDocumentRowAsync` → status `InProgress`. If the process crashes before file move, the next run detects the incomplete state and can retry or skip
- **Sync**: `SyncRunLock` has an `ExpiresAtUtc` — if a worker crashes mid-run, the lock auto-expires and the next cycle can proceed
- **Voicenter**: Annex write + `NispahWriteLog` are in sequence. If crash occurs after Odcanit write but before log persistence, next cycle will attempt a duplicate write (harmless — Odcanit SP handles gracefully)

---

## 8. Daily Summary Email

**What it does in simple terms:**  
Every morning, the system sends an email summarizing yesterday's activity — how many cases were created, documents processed, and any errors that need attention.

**Sent at:** Configurable Israel time (`Email:DailySummaryTimeIsrael`, default `"08:00"`)

**Sections:**

1. **Cases Created** — Count and sample list of new cases onboarded to Monday
2. **Hearings Synced** — Count of hearing updates pushed to Monday
3. **Items Updated** — Total Monday items updated
4. **Sync Failures** — Grouped by case and root cause, with counts
5. **Document Ingestion Failures** — Failed `MondayDocumentImports` with TikNumber, column, error, timestamps
6. **Voicenter Call Summaries** — CDR entries fetched, call details fetched, annexes written, skipped counts, failed writes with sample CallIDs
7. **System Notes** — Circuit breaker alerts, unusually high failure counts

**Data sources:**
- `MondayItemMappings` (created in window)
- `HearingNearestSnapshots` (synced in window)
- `SyncFailures` (in window)
- `MondayDocumentImports` (failed in window)
- `NispahWriteLogs` (Voicenter writes/failures in window)
- `SyncRunMetrics` (circuit breaker, run failure counts)
- `VoicenterCallSummaryWorker.LastRunResult` (last-run counters)

---

## 9. Alerting and Monitoring

### Critical Alert Emails

**What it does in simple terms:**  
The system sends immediate email alerts when something goes seriously wrong, like a worker crash or a database connection failure.

**Alert mechanism:** `IEmailNotifier.QueueCriticalAlert` → queued, deduplicated, rate-limited by `EmailNotifier`.

**Rate limiting:**
- `Email:MaxEmailsPerHour` (default 10)
- `Email:DedupWindowMinutes` (default 60)
- Per-alert fingerprinting via `EmailAlertDedups` table
- Voicenter-specific: `FailureAlertCooldownMinutes` (default 60) for worker-level alerts

**Alert sources:**

| Source | Trigger |
|--------|---------|
| `SyncWorker` | SQL connection failures during sync |
| `VoicenterCallSummaryWorker` | Unhandled exception escapes cycle (`AlertOnUnhandledException`) |
| `VoicenterCallSummaryWorker` | No successful cycle for `StaleWorkerThresholdHours` (`AlertOnStaleWorker`) |
| `DocumentIngestionService` | Critical ingestion failures (alert-worthy errors) |
| `IErrorNotifier` | Worker crash, high failure rate |

### Periodic Digest

`EmailBackgroundService` aggregates non-critical alerts into a periodic digest (interval configurable via `Email:DigestIntervalMinutes`), preventing alert storms for repeated low-severity issues.

### Structured Logging

All workers use structured logging via Serilog with:
- Console sink (development)
- Rolling file sink (production: `C:\Services\Odmon\logs\odmon-.log`, daily rotation, 14 day retention, 100 MB size limit)
- Consistent log prefixes per feature: `SYNC |`, `DOC_INGEST |`, `VOICENTER |`, `VOICENTER_WORKER |`, `HEARING_APPROVAL |`

---

## 10. Configuration and Deployment

### Configuration Hierarchy

Configuration values are resolved in this order (later overrides earlier):

1. `appsettings.json` (base)
2. `appsettings.{Environment}.json` (environment-specific)
3. Environment variables
4. Azure Key Vault (when `KeyVault:Enabled` is true)
5. `dotnet user-secrets` (Development only)

### Configuration Sections

| Section | Purpose | Key Settings |
|---------|---------|-------------|
| `Monday` | Monday.com board and column configuration | `ApiToken`, `BoardId`, `CasesBoardId`, 90+ column IDs, `ReviveInactiveItems`, `MaxRetryAttempts`, `CircuitBreakerFailureThreshold` |
| `Sync` | Main sync worker behavior | `Enabled`, `DryRun`, `MaxItemsPerRun`, `IntervalSeconds`, `ListenerUpdateOnly`, `ListenerCreationCutoffDate` |
| `Onboarding` | New case cooling period | `CoolingPeriodDays` (default 3) |
| `OdcanitLoad` | Case loading allowlist | `EnableAllowList`, `TikCounters[]`, `TikNumbers[]` |
| `OdcanitWrites` | Odcanit write-back control | `Enable`, `DryRun` |
| `MondayDocumentIngestion` | Document ingestion pipeline | `Enabled`, `BoardId`, `InboxPath`, `MaxFileSizeBytes`, `AllowedExtensions[]`, `DeniedExtensions[]`, `Columns[]`, `ColumnMaxFileSizeBytes{}`, `IntervalSeconds`, `MaxRetryCount` |
| `MondayDocumentIngestion:AccidentStory` | Accident story annex | `Enabled`, `WriteEnabled`, `NispahType`, `Columns[]` |
| `MondayDocumentIngestion:TasksSource` | Tasks board ingestion | `Enabled`, `BoardId`, `FileColumnId`, `TikNumberColumnId`, `TaskStatusColumnId`, `SuccessStatusLabel` |
| `OdcanitDocuments` | Odcanit document SP defaults | `CategoryCounter`, `SubCategoryCounter`, `DocStatus`, `DocType`, `WriterCounter`, `OwnerCounter`, `Metapel` |
| `NispahWriter` | Annex write guardrails | `MaxCreatesPerRun`, `MaxCreatesPerMinute`, `DeduplicationWindowMinutes`, `CommandTimeoutSeconds` |
| `VoicenterCallSummaries` | Voicenter integration | `Enabled`, `IntervalHours`, `LookbackHours`, `NispahTypeName`, `OnlyAnsweredCalls`, `MinimumDurationSeconds`, `ThrottleMs`, `TestMode`, `TestCallId`, `AlertOnUnhandledException`, `AlertOnStaleWorker`, `StaleWorkerThresholdHours`, `FailureAlertCooldownMinutes`, `WeeklyUsageWarningThreshold`, `WeeklyUsageHardLimit`, `UsageWarningEmailEnabled` |
| `VoicenterBackfill` | One-shot Voicenter call summary backfill (recover missed calls after quota outage) | `Enable`, `FromUtc`, `ToUtc`, `MaxCalls`, `ForceRecheck`, `DryRun` |
| `Email` | SMTP and alerting | `Enabled`, `SmtpHost`, `SmtpPort`, `UseTls`, `Username`, `Recipients[]`, `MaxEmailsPerHour`, `DedupWindowMinutes`, `DigestIntervalMinutes`, `DailySummaryTimeIsrael` |
| `HearingBackfill` | Bulk hearing import | `Enable`, `SourceTable`, `BoardId`, `BatchSize` |
| `HearingApprovalBackfill` | Historical approval backfill | `Enable`, `DryRun`, `MaxItems`, `OnlyTikCounters`, `ThrottleMs` |
| `Testing` | Test case source | `Enable`, `Source`, `TikCounters[]`, `TableName` |
| `OdmonTestCases` | E2E test cases | `Enable`, `MaxId`, `OnlyIds` |
| `Safety` | Test-data guardrails | `TestBoardId`, `AllowedTikNamePrefix`, `AllowedTikNumberPrefixes[]`, `AllowedTikCounters[]` |
| `Serilog` | Structured logging | Sinks (Console, File), minimum levels, overrides, file rotation |
| `ConnectionStrings` | Database connections | `IntegrationDb`, `OdcanitDb` (key references, not literal strings) |
| `KeyVault` | Azure Key Vault | `VaultUrl`, `Enabled` (in code, not always in JSON) |
| `Secrets` | Local secret fallbacks | `Monday__ApiToken`, connection string keys (for local dev only) |

### Deployment

- **Runtime:** .NET 8, deployed as a Windows Service (`UseWindowsService()`)
- **Database migrations:** EF Core Code First on `IntegrationDbContext`; run `dotnet ef database update` before deployment
- **File system:** Requires write access to inbox path (`MondayDocumentIngestion:InboxPath`) and Odcanit document directories
- **Log directory:** `C:\Services\Odmon\logs\` (configurable via Serilog)
- **Network:** Outbound HTTPS to Monday.com API, Voicenter API, and SMTP (Office365); inbound SQL Server connections to both databases

---

## 11. Secret Management

**What it does in simple terms:**  
Passwords and API keys are never stored in the configuration file. They're kept in a secure vault and only loaded at runtime.

**Architecture:** `ISecretProvider` with `CompositeSecretProvider` pattern.

**Provider chain (in priority order):**

| Provider | Environment | Source |
|----------|------------|--------|
| `UserSecretsProvider` | Development only | `dotnet user-secrets` → `Secrets:{key}` in config |
| `AzureKeyVaultSecretProvider` | When `KeyVault:Enabled` | Azure Key Vault via `SecretClient` |
| `EnvironmentSecretProvider` | Always | `Environment.GetEnvironmentVariable(key)` |

**Required secrets:**

| Key | Purpose |
|-----|---------|
| `Monday__ApiToken` | Monday.com API authentication |
| `IntegrationDb__ConnectionString` | Integration database connection |
| `OdcanitDb__ConnectionString` | Odcanit database connection |
| `Voicenter__Code` | Voicenter CDR API code |
| `Voicenter__BearerToken` | Voicenter call detail API Bearer token |
| `Email:Username` / SMTP password | Email sending (resolved from config, not secret store) |

**Startup validation:** `ValidateRequiredSecretsAsync` checks critical secrets at boot and fails fast if missing.

---

## 12. Testing Modes

**What it does in simple terms:**  
The system has built-in safety modes for testing — you can run it against fake data or restrict it to specific cases, so you never accidentally modify real case files during development.

### Test Case Sources

| Mode | Config | Behavior |
|------|--------|----------|
| Production | Default | `OdcanitCaseSource` reads from real Odcanit views |
| Integration test | `Testing:Enable=true` | `IntegrationTestCaseSource` reads from configurable table in Integration DB |
| E2E test | `OdmonTestCases:Enable=true` | `OdmonTestCasesReader` reads from `dbo.OdmonTestCases` with Hebrew column mapping |

### Safety Guardrails

- `TestSafetyPolicy` restricts sync to allowlisted TikCounters/TikNumbers/name prefixes
- `GuardOdcanitReader` wraps the real reader and throws on all calls when testing mode blocks Odcanit access
- `Safety:AllowedTikNumberPrefixes` (e.g. `["9/999"]`) prevents accidental writes to production cases
- `OdcanitWrites:DryRun` disables all Odcanit stored procedure calls
- `Sync:DryRun` disables Monday API mutations

### Voicenter Test Mode

When `VoicenterCallSummaries:TestMode=true` and `TestCallId` is set, only that single call is processed with enhanced diagnostic logging (summary presence, summary length).

---

## Appendix: File Structure

```
Odmon/
├── Configuration/          # Settings classes (bound from appsettings.json)
├── Data/                   # IntegrationDbContext, MondayMappingReadService
├── docs/                   # This documentation and feature-specific notes
├── Migrations/             # EF Core migrations for IntegrationDb
├── Models/                 # Entity models (Integration + Odcanit views)
├── Monday/                 # Monday.com client, metadata, settings
├── OdcanitAccess/          # Odcanit DB context, readers, writers
├── Security/               # ISecretProvider implementations
├── Services/               # Business logic services
├── Voicenter/              # Voicenter API client and models
├── Workers/                # Background service workers
├── Program.cs              # Host builder, DI registration, startup validation
├── appsettings.json        # Base configuration
└── appsettings.Development.json  # Development overrides
```
