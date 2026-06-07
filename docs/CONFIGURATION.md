# ODMON Configuration Guide

## Overview

This document describes all configuration keys required for the ODMON Worker, and how they map to environment variables and Azure Key Vault secrets.

## Configuration Hierarchy

Configuration is loaded in the following order (later sources override earlier ones):

1. `appsettings.json` (base settings)
2. `appsettings.{Environment}.json` (e.g., `appsettings.Production.json`)
3. User Secrets (Development only)
4. Environment Variables
5. Azure Key Vault (if `KeyVault:Enabled=true`)

## Critical Configuration Keys

### Monday.com Settings

| Config Key | Environment Variable | Type | Required | Default | Description |
|------------|---------------------|------|----------|---------|-------------|
| `Monday:BoardId` | `Monday__BoardId` | long | **YES** | 0 | Primary Monday.com board ID. **MUST NOT BE 0**. |
| `Monday:CasesBoardId` | `Monday__CasesBoardId` | long | No | 0 | Alternative board ID for cases. If set and non-zero, used instead of BoardId for case sync. |
| `Monday:ApiToken` | `Monday__ApiToken` | string | **YES** | - | Monday.com API token. Should be stored in Key Vault or environment variable, not in config files. |
| `Monday:ToDoGroupId` | `Monday__ToDoGroupId` | string | No | - | Monday group ID for tasks. |
| `Monday:TestGroupId` | `Monday__TestGroupId` | string | No | - | Monday group ID for test items. |

### Monday Column ID Settings

These settings map Odcanit fields to specific Monday.com column IDs. They have sensible defaults in `MondaySettings.cs` but can be overridden via configuration.

| Config Key | Environment Variable | Type | Default | Description |
|------------|---------------------|------|---------|-------------|
| `Monday:PlaintiffIdColumnId` | `Monday__PlaintiffIdColumnId` | string | `text_mm1sq8bs` | Column for ת.ז תובע (plaintiff ID). |
| `Monday:DefendantIdColumnId` | `Monday__DefendantIdColumnId` | string | `text_mm1ye90m` | Column for ת.ז נתבע (defendant ID). |
| `Monday:DefendantFaxColumnId` | `Monday__DefendantFaxColumnId` | string | `null` | Defendant fax — no dedicated Monday column. Set null to avoid conflict with ThirdPartyLawyerFaxColumnId. |
| `Monday:ThirdPartyLawyerEmailColumnId` | `Monday__ThirdPartyLawyerEmailColumnId` | string | `email_mkwj4mmk` | Column for דוא"ל עו"ד צד ג' (third-party lawyer email). |
| `Monday:ThirdPartyLawyerFaxColumnId` | `Monday__ThirdPartyLawyerFaxColumnId` | string | `text_mkxe2zay` | Column for פקס עו"ד צד ג' (third-party lawyer fax). |
| `Monday:AdditionalIdentificationColumnId` | `Monday__AdditionalIdentificationColumnId` | string | `text_mm1gvfd5` | Column for זיהוי נוסף (additional identification). |
| `Monday:ClaimVersionsColumnId` | `Monday__ClaimVersionsColumnId` | string | `long_text_mm1gsvg0` | Column for גרסאות תביעה (claim versions). |
| `Monday:PleadingDeadlineDateColumnId` | `Monday__PleadingDeadlineDateColumnId` | string | `date_mm1gex5r` | Column for תאריך אחרון להגשת כתב הטענות (pleading deadline date). |
| `Monday:DefenseVersionsColumnId` | `Monday__DefenseVersionsColumnId` | string | `long_text_mm1gxq01` | Column for גרסאות הגנה (defense versions). |
| `Monday:InsuranceCompany1ColumnId` | `Monday__InsuranceCompany1ColumnId` | string | `text_mm1qnbwn` | Column for חברות ביטוח 1 (insurance company 1 name). |
| `Monday:InsuranceCompany1AddressColumnId` | `Monday__InsuranceCompany1AddressColumnId` | string | `text_mm1q8a83` | Column for כתובת חברת ביטוח 1 (insurance company 1 address). |
| `Monday:InsuranceCompany2ColumnId` | `Monday__InsuranceCompany2ColumnId` | string | `text_mm1gy0q4` | Column for חברת ביטוח 2 (insurance company 2). |
| `Monday:InsuranceCompany2AddressColumnId` | `Monday__InsuranceCompany2AddressColumnId` | string | `text_mm1gmta2` | Column for כתובת חברת ביטוח 2 (insurance company 2 address). |
| `Monday:ProceedingTypeColumnId` | `Monday__ProceedingTypeColumnId` | string | `text_mm1gsp8k` | Column for סוג הליך (proceeding type). |
| `Monday:PaymentDueAmountColumnId` | `Monday__PaymentDueAmountColumnId` | string | `numeric_mm1gj81h` | Column for סכום לתשלום (payment due amount). |
| `Monday:ThirdPartyClaimAmountColumnId` | `Monday__ThirdPartyClaimAmountColumnId` | string | `numeric_mky2m5a1` | Column for סכום תביעה צד ג (third-party claim amount). |
| `Monday:ReconstructionFeeAmountColumnId` | `Monday__ReconstructionFeeAmountColumnId` | string | `numeric_mky2yr0y` | Column for דמי כינון (reconstruction fee). |
| `Monday:DeductibleDamageAmountColumnId` | `Monday__DeductibleDamageAmountColumnId` | string | `numeric_mky2ywrf` | Column for השתתפות עצמית לנזק (deductible damage). |
| `Monday:InsuranceBenefitsAmountColumnId` | `Monday__InsuranceBenefitsAmountColumnId` | string | `numeric_mkza3p82` | Column for תגמולי ביטוח (insurance benefits). |

**CRITICAL**: If both `Monday:BoardId` and `Monday:CasesBoardId` are 0, the worker will throw an exception:

```
InvalidOperationException: BoardId is 0 for TikCounter=... Missing config key Monday:BoardId or Monday:CasesBoardId.
```

### Database Connection Strings

| Config Key | Environment Variable | Secret Key | Required | Description |
|------------|---------------------|------------|----------|-------------|
| `ConnectionStrings:IntegrationDb` | - | `IntegrationDb__ConnectionString` | **YES** | Connection string for OdmonIntegration database (worker's own DB). |
| `ConnectionStrings:OdcanitDb` | - | `OdcanitDb__ConnectionString` | **YES** (prod) | Connection string for Odcanit database (source system). Not required in Development. |

**Example Connection String Format:**
```
Server=sql-server.database.windows.net;Database=OdmonIntegration;User Id=username;Password=***;Encrypt=True;TrustServerCertificate=False;
```

### Azure Key Vault (Optional)

| Config Key | Environment Variable | Type | Required | Default | Description |
|------------|---------------------|------|----------|---------|-------------|
| `KeyVault:Enabled` | `KeyVault__Enabled` | bool | No | false | Explicitly enable Key Vault. If false, Key Vault is never accessed. |
| `KeyVault:VaultUrl` | `KeyVault__VaultUrl` | string | No | - | Azure Key Vault URL (e.g., `https://your-vault.vault.azure.net/`). Only used if `Enabled=true`. |

**Note**: Key Vault is completely optional. If `KeyVault:Enabled=false` or not set, the worker will not attempt to connect to Key Vault.

### Testing Mode

| Config Key | Environment Variable | Type | Required | Default | Description |
|------------|---------------------|------|----------|---------|-------------|
| `Testing:Enable` | `Testing__Enable` | bool | No | false | Enable test mode (reads from `OdmonTestCases` table instead of Odcanit). |
| `Testing:Source` | `Testing__Source` | string | No | - | Test data source identifier. |
| `Testing:TikCounters` | `Testing__TikCounters` | int[] | No | [] | List of TikCounters to process in test mode. |
| `Testing:TableName` | `Testing__TableName` | string | No | - | Test table name in IntegrationDb. |

### Safety Policy

| Config Key | Environment Variable | Type | Required | Default | Description |
|------------|---------------------|------|----------|---------|-------------|
| `Safety:TestBoardId` | `Safety__TestBoardId` | long | No | 0 | Monday board ID for test items. |
| `Safety:TestMode` | `Safety__TestMode` | bool | No | false | Enable test mode safety checks. |

## Environment Variable Naming Convention

Configuration keys use `:` as a separator (e.g., `Monday:BoardId`).

Environment variables replace `:` with `__` (double underscore):
- `Monday:BoardId` → `Monday__BoardId`
- `KeyVault:Enabled` → `KeyVault__Enabled`
- `ConnectionStrings:IntegrationDb` → Cannot be set via env var directly; use secret key instead.

## Secret Keys (for ISecretProvider)

The worker uses an `ISecretProvider` abstraction that tries multiple sources:

1. **User Secrets** (Development only)
2. **Azure Key Vault** (if enabled)
3. **Environment Variables**
4. **Configuration fallback** (appsettings.json)

Secret keys use `__` as a separator (environment variable format):

| Secret Key | Description | Config Fallback |
|------------|-------------|-----------------|
| `Monday__ApiToken` | Monday.com API token | `Monday:ApiToken` |
| `IntegrationDb__ConnectionString` | IntegrationDb connection string | `ConnectionStrings:IntegrationDb` |
| `OdcanitDb__ConnectionString` | Odcanit DB connection string | `ConnectionStrings:OdcanitDb` |

**Recommended Practice**:
- Store secrets in Azure Key Vault (production) or User Secrets (development)
- Do NOT store secrets in appsettings files
- Use environment variables as a fallback for non-Key Vault deployments

## Production Server Setup

### Minimum Required Configuration

Set these environment variables on the production server:

```bash
# Monday.com Board ID (CRITICAL)
Monday__BoardId=5035534500

# Connection Strings
IntegrationDb__ConnectionString="Server=...;Database=OdmonIntegration;User Id=...;Password=...;"
OdcanitDb__ConnectionString="Server=...;Database=Odcanit;User Id=...;Password=...;"

# Monday API Token
Monday__ApiToken="your-monday-api-token"

# Optional: Key Vault (if using Azure Key Vault)
KeyVault__Enabled=true
KeyVault__VaultUrl="https://your-vault.vault.azure.net/"
```

### With Azure Key Vault

If using Key Vault, store secrets in the vault and set:

```bash
# Enable Key Vault
KeyVault__Enabled=true
KeyVault__VaultUrl="https://your-vault.vault.azure.net/"

# BoardId still needs to be in environment or config (not secret)
Monday__BoardId=5035534500
```

Then create these secrets in Key Vault:
- `Monday--ApiToken` (Key Vault uses `--` instead of `__`)
- `IntegrationDb--ConnectionString`
- `OdcanitDb--ConnectionString`

**Note**: Azure Key Vault automatically converts `--` to `:` in configuration keys.

## Development Setup

### Option 1: User Secrets (Recommended for Development)

```bash
# Initialize user secrets
dotnet user-secrets init

# Set secrets
dotnet user-secrets set "Monday__ApiToken" "your-test-token"
dotnet user-secrets set "IntegrationDb__ConnectionString" "Server=localhost;Database=OdmonIntegration;Integrated Security=true;"
```

Set non-secret configuration in `appsettings.Development.json`:

```json
{
  "Monday": {
    "BoardId": 5035534500
  }
}
```

### Option 2: Environment Variables

Set environment variables in your development environment:

```bash
# PowerShell
$env:Monday__BoardId = "5035534500"
$env:Monday__ApiToken = "your-test-token"
$env:IntegrationDb__ConnectionString = "Server=localhost;Database=OdmonIntegration;Integrated Security=true;"

# Bash/Linux
export Monday__BoardId=5035534500
export Monday__ApiToken=your-test-token
export IntegrationDb__ConnectionString="Server=localhost;Database=OdmonIntegration;Integrated Security=true;"
```

## Validation

### Startup Diagnostics

When the worker starts, it logs a diagnostic line with current configuration:

```
[StartupDiagnostics] Startup Diagnostics: Environment=Production, Monday.BoardId=5035534500, Monday.CasesBoardId=5035534500, KeyVault.Enabled=True, KeyVault.VaultUrl=https://***/***, IntegrationDb=your-server/OdmonIntegration, OdcanitDb=your-server/Odcanit
```

**Check this log line to verify**:
- Environment name is correct
- BoardId is NOT 0
- Database connections point to correct servers
- Key Vault status is as expected

### Configuration Validation Errors

The worker performs validation at startup and will throw exceptions if critical config is missing:

```
InvalidOperationException: Connection string 'IntegrationDb' is not configured. Provide it via secret 'IntegrationDb__ConnectionString'...
```

```
InvalidOperationException: Required secret 'Monday__ApiToken' was not found. Provide it via user-secrets (Development) or Azure KeyVault/Environment Variables (Production).
```

```
InvalidOperationException: BoardId is 0 for TikCounter=900000. Missing config key Monday:BoardId or Monday:CasesBoardId.
```

## Document Ingestion Settings

### MondayDocumentIngestion

| Config Key | Type | Default | Description |
|------------|------|---------|-------------|
| `MondayDocumentIngestion:Enabled` | bool | false | Enable/disable the document ingestion worker. |
| `MondayDocumentIngestion:BoardId` | long | 5088708083 | Monday.com questionnaire board ID to poll. |
| `MondayDocumentIngestion:LinkedCasesBoardId` | long | 5035534500 | Main cases board ID for TikVisualID resolution. |
| `MondayDocumentIngestion:InboxPath` | string | `D:\Odlight\OdmonInbox` | Local directory for temporary file downloads. |
| `MondayDocumentIngestion:MaxFileSizeBytes` | long | 52428800 | Maximum file size (50 MB). |
| `MondayDocumentIngestion:AllowedExtensions` | string[] | `["pdf","jpg","jpeg","png"]` | Allowed file extensions. |
| `MondayDocumentIngestion:Columns` | string[] | `["file_mm0qwtat","file_mkzr2cmr"]` | Monday file column IDs to ingest. |
| `MondayDocumentIngestion:RelationColumnId` | string | `board_relation_mkzenscq` | Board relation column linking to the cases board. |
| `MondayDocumentIngestion:LinkedCaseTikColumnId` | string | `text_mkwe19hn` | Column on the cases board that holds TikVisualID. |
| `MondayDocumentIngestion:IntervalSeconds` | int | 300 | Polling interval in seconds. |
| `MondayDocumentIngestion:MaxRetryCount` | int | 3 | Maximum retry attempts per failed asset. |
| `MondayDocumentIngestion:CommandTimeoutSeconds` | int | 60 | SQL command timeout for SP calls. |

**TasksSource** (optional) — Imports generated PDFs from the Monday Tasks board (משימות, 5035534505):

| Config Key | Type | Default | Description |
|------------|------|---------|-------------|
| `MondayDocumentIngestion:TasksSource:Enabled` | bool | false | Enable Tasks board as additional document source. |
| `MondayDocumentIngestion:TasksSource:BoardId` | long | 5035534505 | Tasks board ID. |
| `MondayDocumentIngestion:TasksSource:TaskStatusColumnId` | string | color_mkwej7ys | Status column. |
| `MondayDocumentIngestion:TasksSource:SuccessStatusLabel` | string | טופס נוצר בהצלחה | Status label indicating ready for import. |
| `MondayDocumentIngestion:TasksSource:FileColumnId` | string | file_mm1bvngc | WORD file column (Word documents only: .docx, .doc). |
| `MondayDocumentIngestion:TasksSource:TikNumberColumnId` | string | lookup_mm19bm4v | Lookup column for TikNumber. |
| `MondayDocumentIngestion:TasksSource:FileWaitTimeoutHours` | int | 2 | Hours to wait for file after status ready; then timeout failure. |
| `MondayDocumentIngestion:TasksSource:ItemsPageLimit` | int | 50 | Max items per API page. |

### OdcanitDocuments

| Config Key | Type | Default | Description |
|------------|------|---------|-------------|
| `OdcanitDocuments:CategoryCounter` | int | 1 | Odcanit document category. |
| `OdcanitDocuments:SubCategoryCounter` | int | 0 | Odcanit document sub-category. |
| `OdcanitDocuments:DocStatus` | int | 1 | Odcanit document status. |
| `OdcanitDocuments:DocType` | int | 8 | Odcanit document type (8 works for PDF and JPG). |
| `OdcanitDocuments:WriterCounter` | int | 1 | Odcanit writer user ID. |
| `OdcanitDocuments:OwnerCounter` | int | 1 | Odcanit owner user ID. |
| `OdcanitDocuments:Metapel` | int | 1 | Odcanit handler ID (sent as comma-separated string). |

### IntegrationDb Tracking Table

Document ingestion state is tracked in `MondayDocumentImports`:
- Dedup key: `(MondayQuestionnaireItemId, ColumnId, AssetId)` — unique index
- Status values: Pending(0), Downloaded(1), SpCreated(2), Copied(3), Verified(4), Success(5), Failed(6)
- Each step is persisted so retries can resume from the last successful step

### MainTik Column Compatibility

The TikCounter resolution from TikVisualID tries column `TikCounter` first, then falls back to `Counter` (SqlException 207 handling). This ensures compatibility across different Odcanit DB versions without schema changes.

## Troubleshooting

### Worker throws "BoardId is 0" exception

**Cause**: `Monday:BoardId` and `Monday:CasesBoardId` are both 0 or not set.

**Fix**: Set environment variable:

```bash
Monday__BoardId=5035534500
```

Or in appsettings.Production.json:

```json
{
  "Monday": {
    "BoardId": 5035534500
  }
}
```

### Worker throws "Required secret 'Monday__ApiToken' was not found"

**Cause**: Monday API token is not configured in any secret source.

**Fix**:
- **Development**: Set user secret: `dotnet user-secrets set "Monday__ApiToken" "your-token"`
- **Production**: Set environment variable or Key Vault secret

### Key Vault connection fails with "retry failed"

**Cause**: Key Vault is trying to connect but URL is invalid or vault doesn't exist.

**Fix**: If you don't want to use Key Vault, disable it:

```bash
KeyVault__Enabled=false
```

Or remove/fix the `KeyVault:VaultUrl` configuration.

### SQL timeout on MondayItemMappings query

**Cause**: BoardId is 0 in runtime, causing full table scans.

**Fix**: See "BoardId is 0" fix above. Also verify index exists (see `SQL_Diagnostics.md`).

## NetCourt Decision Alerts

`NetCourtDecisionAlerts` controls the independent Odcanit `vwNetCourtDocs` decision-email worker.

| Setting | Default | Description |
|---|---:|---|
| `Enabled` | `false` | Enables the dedicated NetCourt polling worker. |
| `IntervalSeconds` | `300` | Polling interval. |
| `LookbackDays` | `7` | Overlap window; durable document tracking provides final deduplication. |
| `BaselineOnlyOnFirstRun` | `true` | Records existing DocType 2/3 rows without emailing on the first run. |
| `EmailMode` | `Test` | `Test` sends only to `TestRecipient`; `Live` sends to the routed employee. |
| `TestRecipient` | `odmon@ezer-law.com` | Actual recipient used in Test mode. |
| `ClientNumberToRecipientEmail` | configured map | Client-number-to-employee routing. |
| `FallbackRecipientEnabled` | `false` | Allows global `Email:Recipients` only when explicitly enabled. |

Only `vwNetCourtDocs` rows with `DocType IN (2, 3)` are eligible. Filenames and free-text fields are not used for classification.

## Configuration File Examples

### appsettings.json (Base)

```json
{
  "Monday": {
    "BoardId": 0,
    "CasesBoardId": 0,
    "ToDoGroupId": "topics",
    "CaseNumberColumnId": "text_mkwe19hn"
  },
  "KeyVault": {
    "Enabled": false,
    "VaultUrl": ""
  },
  "Testing": {
    "Enable": false
  }
}
```

### appsettings.Production.json

```json
{
  "Monday": {
    "BoardId": 5035534500,
    "CasesBoardId": 5035534500
  },
  "KeyVault": {
    "Enabled": true,
    "VaultUrl": "https://odmon-prod-kv.vault.azure.net/"
  }
}
```

### appsettings.Development.json

```json
{
  "Monday": {
    "BoardId": 5035534500,
    "CasesBoardId": 5035534500
  },
  "KeyVault": {
    "Enabled": false
  },
  "ConnectionStrings": {
    "IntegrationDb": "Server=localhost;Database=OdmonIntegration;Integrated Security=true;",
    "OdcanitDb": "Server=localhost;Database=Odcanit;Integrated Security=true;"
  }
}
```

**Note**: Never commit actual secrets (API tokens, passwords) to appsettings files. Use User Secrets or Key Vault.
