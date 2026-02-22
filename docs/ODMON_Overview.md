# ODMON Architecture & Sync Flow Overview

## Project Purpose

ODMON is a .NET 8 Worker Service that synchronizes case data from the Odcanit/Odlight database system into Monday.com boards, and ingests documents from Monday.com questionnaire boards back into Odcanit. The system maintains a one-to-one mapping between Odcanit cases (identified by TikNumber) and Monday.com items, ensuring data consistency and enabling updates to existing items rather than creating duplicates.

## High-Level Architecture

### Core Components

- **SyncWorker**: Background service that runs on a configurable interval (default: 30 seconds)
  - Creates a scoped service provider for each sync run
  - Invokes SyncService to perform the synchronization
  - Handles errors gracefully and continues running

- **SyncService**: Main orchestration service that coordinates the sync process
  - Reads configuration (test mode, dry run, date filters, etc.)
  - Fetches cases from Odcanit via IOdcanitReader
  - Enriches cases with related data
  - Maps Odcanit data to Monday.com column values
  - Determines create vs. update operations
  - Executes Monday.com API calls
  - Maintains mapping records in IntegrationDbContext

- **SqlOdcanitReader**: Reads and enriches case data from Odcanit database
  - Queries cases filtered by creation date
  - Enriches cases with multiple data sources:
    - Client contact information (phone, email, address)
    - Case sides (plaintiff, defendant, third party)
    - Diary events (court hearings, dates)
    - Legal user data from UserData view vwExportToOuterSystems_UserData (policy holder, driver, vehicle info)
    - Hozlap main data (court case numbers)

- **MondayClient**: GraphQL API client for Monday.com
  - Creates new items
  - Updates existing items (column values and names)
  - Queries items by column value to find existing items

- **MondayMetadataProvider**: Resolves Monday.com column IDs dynamically
  - Caches column metadata by board ID and column title
  - Enables column mapping without hard-coded IDs for some fields

- **IntegrationDbContext**: SQL Server database for sync state
  - Stores MondayItemMapping records (TikCounter, TikNumber, MondayItemId, BoardId)
  - Stores MondayDocumentImport records for document ingestion tracking and dedup
  - Maintains SyncLog entries for audit trail
  - Tracks test mode flags and sync timestamps

- **DocumentIngestionWorker / DocumentIngestionService**: Monday → Odcanit document ingestion
  - Polls a questionnaire board for file attachments
  - Downloads files to a local inbox, calls `dbo.ProcDocuments_AddNewDocument` with MoveFile=3
  - Copies files to the DestPath returned by the SP, verifies, and cleans up
  - Deduplicates by (MondayQuestionnaireItemId, ColumnId, AssetId) in MondayDocumentImports table
  - Nispah (e.g. accident story) writes use Integration DB dedup table `NispahDeduplications`; duplicate-key (2601/2627) is treated as skip and does not trigger critical alerts. See [NISPAH_DEDUP_AND_INCIDENT_ALERTS.md](NISPAH_DEDUP_AND_INCIDENT_ALERTS.md).

- **OdcanitDocumentWriter**: Calls Odcanit stored procedures for document creation
  - Resolves TikCounter from TikVisualID via dbo.MainTik (tries column "TikCounter" then "Counter" for DB compatibility)

## Data Flow

### 1. Sync Trigger
- SyncWorker runs on periodic timer (configurable interval)
- Each run generates a unique run ID for logging

### 2. Configuration & Mode Selection
- Reads sync configuration (enabled, dry run, max items, date filter)
- Determines test mode vs. production mode
- Selects Monday.com board and group based on mode:
  - Test mode: Uses test board ID and test group
  - Production mode: Uses production board ID and default group

### 3. Case Retrieval
- Calls SqlOdcanitReader.GetCasesCreatedOnDateAsync()
- Filters cases by creation date (tsCreateDate) when UseTodayOnly is enabled
- Returns list of OdcanitCase objects

### 4. Case Enrichment (Multi-Step Process)
- **Client Enrichment**: Loads client contact info (phone, email, address) from vwExportToOuterSystems_UserData
- **Sides Enrichment**: Loads case sides (plaintiff, defendant, third party) from vwSides
- **Diary Events Enrichment**: Loads court hearings and events from vwDiaryEvents
- **User Data Enrichment**: Loads legal user data (UserData view vwExportToOuterSystems_UserData, PageName = "פרטי תיק נזיקין מליגל") including:
  - Policy holder information (name, ID, address, phone, email)
  - Driver information (name, ID, mobile phone)
  - Vehicle numbers (main car, second car, third party car)
  - Insurance company details
  - Third party employer and lawyer information
- **Hozlap Main Data Enrichment**: Loads court case numbers from vwHozlapFormsData_TikMainData

### 5. Mapping & Deduplication Logic
- For each case, checks if a Monday.com item already exists:
  - First: Queries IntegrationDbContext by TikNumber + BoardId (preferred) or TikCounter (fallback)
  - If no mapping found and TikNumber exists: Queries Monday.com API by TikNumber in "מספר תיק" column
  - If existing item found: Creates mapping record and proceeds to update
  - If no existing item: Proceeds to create new item

### 6. Safety & Filtering
- Applies safety policy checks:
  - Test mode: Only processes test-compatible cases
  - Production mode: Only processes cases in "demo window" or explicitly allowed test cases
- Skips cases that don't meet safety criteria

### 7. Item Name Construction
- Builds Monday.com item name from:
  - Client name and/or policy holder name
  - TikNumber (or TikCounter as fallback)
- In test mode: Prepends "[TEST]" prefix to item name

### 8. Column Value Mapping
- Builds JSON payload for Monday.com column values:
  - Case identification: TikNumber, ClientNumber, ClaimNumber
  - Dates: Case open date, event date, close date, hearing date, complaint received date
  - Contact information: Client, policy holder, driver, plaintiff, defendant, third party (phones, emails, addresses)
  - Financial data: Requested claim amount, proven amount, judgment amount
  - Court information: Court name, city, case number, judge name, hearing details
  - Vehicle information: Main car number, second car, third party car
  - Legal parties: Attorney name, defense street, claim street
  - Status fields: Case status, task type, document type, court name
- **Document Type Logic**: Determined by client number:
  - Client number = 1 → "כתב הגנה" (defense)
  - Client number in {4, 7, 9} OR ≥ 100 → "כתב תביעה" (claim)
  - Otherwise: Left empty
- Phone numbers are normalized to E.164 format (+972...) for Israeli numbers
- Some column IDs are resolved dynamically by title (e.g., policy holder name column)

### 9. Create or Update Decision
- **If no mapping exists**:
  - Creates new Monday.com item via create_item mutation
  - Creates MondayItemMapping record with TikCounter, TikNumber, BoardId, MondayItemId
  - Sets initial status to "חדש" (new)
- **If mapping exists**:
  - Compares OdcanitVersion (tsModifyDate) to detect changes
  - Compares item name checksum to detect name changes
  - If changes detected: Updates item via change_multiple_column_values mutation
  - Updates mapping record with new version and timestamp
  - Updates item name separately if changed

### 10. Logging & Audit
- Logs each case action (created, updated, skipped, failed)
- Writes SyncLog entries to IntegrationDbContext
- Tracks statistics per run (created, updated, skipped, failed counts)

## Key Data Models

### OdcanitCase
- Core case entity with TikCounter (internal ID) and TikNumber (display number)
- Contains all enriched fields: client info, sides, policy holder, driver, court data, financial amounts
- Timestamps: tsCreateDate, tsModifyDate for change detection

### MondayItemMapping
- Links Odcanit cases to Monday.com items
- Stores: TikCounter, TikNumber, BoardId, MondayItemId
- Tracks: LastSyncFromOdcanitUtc, OdcanitVersion, MondayChecksum (item name), IsTest flag
- Indexed by TikCounter (unique) and (TikNumber, BoardId) for efficient lookups

### SyncLog
- Audit trail of sync operations
- Stores: CreatedAtUtc, Source, Level, Message, Details (JSON)

## Configuration

### Sync Settings
- Enabled: Enable/disable sync
- DryRun: Preview mode without making changes
- MaxItemsPerRun: Limit number of cases processed per run
- UseTodayOnly: Filter to cases created today (default: true)
- IntervalSeconds: Time between sync runs (default: 30)

### Safety Settings
- TestMode: Enable test mode (uses test board, adds [TEST] prefix)
- TestBoardId: Monday.com board ID for test mode

### Monday Settings
- ApiToken: Monday.com API authentication token (from secrets)
- CasesBoardId: Production board ID
- TestGroupId: Group ID for test items
- Column IDs: Mapping of field types to Monday.com column IDs

## Security & Secrets

- Secrets are resolved via ISecretProvider composite pattern:
  - Development: User secrets → Environment variables
  - Production: Azure Key Vault (Managed Identity) → Environment variables
- Required secrets:
  - Monday__ApiToken
  - IntegrationDb__ConnectionString
  - OdcanitDb__ConnectionString (production only)

## Error Handling

- SyncWorker catches exceptions and logs errors without stopping the service
- SyncService logs failed operations but continues processing other cases
- Monday.com API errors are logged with full details
- Failed operations are tracked in SyncLog with error messages

## Document Ingestion (Monday → Odcanit)

### Overview

ODMON includes a document ingestion pipeline that downloads file attachments from a Monday.com questionnaire board ("שאלון פתיחת לקוח", board 5088708083) and creates corresponding document records in Odcanit.

### Pipeline Steps

1. **Fetch**: Query questionnaire board items with file columns and board relation column
2. **Resolve**: Follow the board relation to the linked case item, read TikVisualID, resolve TikCounter from dbo.MainTik
3. **Download**: Stream each file asset to a local inbox directory (e.g. `D:\Odlight\OdmonInbox\Cases\<TikVisualID>\`)
4. **SP Call**: Execute `dbo.ProcDocuments_AddNewDocument` with `@MoveFile = 3` — creates the Documents row and returns DestPath + DocCounter without invoking xp_cmdshell
5. **Copy**: Copy the file from inbox to the DestPath returned by the SP
6. **Verify**: Confirm destination file exists and size matches source
7. **Cleanup**: Delete the inbox file after successful verification

### TikCounter Resolution Compatibility

The `ResolveTikCounterAsync` method handles different Odcanit DB versions:
- First tries: `SELECT TOP 1 TikCounter FROM dbo.MainTik WHERE VisualID = @TikVisualID`
- If column "TikCounter" doesn't exist (SqlException 207) or returns null, falls back to: `SELECT TOP 1 Counter FROM dbo.MainTik WHERE VisualID = @TikVisualID`
- Logs which column path was used for diagnostics

### Deduplication & Tracking

All ingestion state is tracked in the `MondayDocumentImports` table in IntegrationDb:
- Unique constraint on (MondayQuestionnaireItemId, ColumnId, AssetId) prevents duplicate Odcanit documents
- Status progression: Pending → Downloaded → SpCreated → Copied → Verified → Success
- Failed imports are retried up to a configurable limit; each step is persisted for idempotent retry

### Failure Handling

- Failures trigger urgent email alerts via the existing EmailNotifier infrastructure
- The AlertSent flag prevents duplicate alerts per record
- Transient failures (network, SP timeout) are retried on the next polling cycle

## Test Mode Behavior

- Uses separate Monday.com board (TestBoardId)
- Adds "[TEST]" prefix to item names
- Only processes test-compatible cases (based on safety policy)
- Prevents creating items for non-test cases that already have production mappings

