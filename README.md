# Odmon Worker Service

A .NET 8 Worker Service that synchronizes data between Odcanit and Monday.com, and ingests documents from Monday.com questionnaire boards into Odcanit.

## Project Structure

- **Models/**: Data models (OdcanitCase, MondayItemMapping, MondayDocumentImport, SyncLog, etc.)
- **Data/**: IntegrationDbContext for storing sync mappings, logs, and document import tracking
- **OdcanitAccess/**: Database context and readers for Odcanit system
- **Monday/**: Monday.com API client
- **Services/**: SyncService, DocumentIngestionService, OdcanitDocumentWriter, and supporting services
- **Workers/**: Background worker services (SyncWorker, DocumentIngestionWorker, EmailBackgroundService)
- **Configuration/**: Settings classes for all configurable features

## Configuration & Secrets

The worker no longer reads secrets from the checked-in `appsettings*.json` files.  
Secrets are resolved through `ISecretProvider`, which checks (in order):

1. **Development** – `dotnet user-secrets` (section `Secrets:`) and then environment variables.  
2. **Production** – Azure Key Vault (via Managed Identity) and then environment variables.

### Required Secret Keys

- `Monday__ApiToken`
- `IntegrationDb__ConnectionString`
- `OdcanitDb__ConnectionString` (required in production only)

Non-secret settings such as `Monday:BoardId` and `Safety:*` remain in the normal configuration files.

### Local Development Setup

1. Initialize user secrets (run once):
   ```bash
   dotnet user-secrets init
   ```
2. Set the required secrets:
   ```bash
   dotnet user-secrets set "Secrets:Monday__ApiToken" "<your-token>"
   dotnet user-secrets set "Secrets:IntegrationDb__ConnectionString" "<sql-connection-string>"
   dotnet user-secrets set "Secrets:OdcanitDb__ConnectionString" "<optional-odcanit-connection>"
   ```
3. Alternatively, export environment variables using the same keys (e.g. `Monday__ApiToken`).
4. Run the worker:
   ```bash
   dotnet run
   ```

If a required secret is missing, the app fails fast at startup with a descriptive error.

### Azure / Production Setup

1. Create an Azure Key Vault and store secrets with the exact names listed above.
2. Enable Managed Identity on the hosting resource (App Service/VM/AKS/etc.) and grant **Get** access to the Key Vault secrets.
3. Set `KeyVault:VaultUrl` in configuration (environment variable or app settings) to the vault URI.
4. Ensure outbound networking allows the worker to reach the Key Vault endpoint.
5. CI/CD: never commit secrets; provision them directly in Key Vault or via your deployment pipeline.

The worker logs which provider supplied a value (never the secret itself). Missing secrets are reported with masked output.

## Document Ingestion (Monday → Odcanit)

ODMON can ingest file attachments from a Monday.com questionnaire board into Odcanit's document system. This feature is **disabled by default**.

### How it works

1. The `DocumentIngestionWorker` polls the questionnaire board (configurable, default: `5088708083`) on a periodic interval.
2. For each item, it reads file columns (`file_mm0qwtat`, `file_mkzr2cmr`) and resolves the linked case via the board relation column.
3. The linked case's TikVisualID (e.g. `9/220`) is read from the main cases board, then resolved to a TikCounter via `dbo.MainTik` in Odcanit (tries column `TikCounter` first, falls back to `Counter` for DB compatibility).
4. Each file asset is downloaded to a local inbox directory, then `dbo.ProcDocuments_AddNewDocument` is called with `@MoveFile = 3` (DB row only, no xp_cmdshell).
5. The application copies the file to the `DestPath` returned by the SP, verifies the copy (existence + size match), then deletes the inbox file.
6. Tracking records in `MondayDocumentImports` (IntegrationDb) ensure deduplication by `(MondayQuestionnaireItemId, ColumnId, AssetId)` and enable retry on transient failures.
7. Failures trigger urgent email alerts with full context (TikVisualID, TikCounter, AssetId, filename, error).

### Configuration

Enable in `appsettings.json` or environment variables:

```json
{
  "MondayDocumentIngestion": {
    "Enabled": true,
    "BoardId": 5088708083,
    "InboxPath": "D:\\Odlight\\OdmonInbox",
    "IntervalSeconds": 300
  },
  "OdcanitDocuments": {
    "CategoryCounter": 1,
    "DocType": 8,
    "WriterCounter": 1,
    "OwnerCounter": 1,
    "Metapel": 1
  }
}
```

See `docs/CONFIGURATION.md` for the full list of keys.

## Running the Service

```bash
dotnet run
```

Or build and run:

```bash
dotnet build
dotnet run
```

