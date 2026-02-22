# IntegrationDb (IntegrationDbContext) migrations

ODMON uses EF Core with **IntegrationDbContext** against the Integration database (connection key: `IntegrationDb` / `IntegrationDb__ConnectionString`). This doc covers applying migrations and verifying Nispah tables.

## Apply migrations (dev repo folder)

Run from the **dev repo** folder (the one that contains `Odmon.Worker.csproj`). Do **not** run `dotnet ef` from the deployed service folder (e.g. `C:\Services\Odmon\app\`) — that folder may not contain the project file and will fail with "No project was found".

```powershell
cd "C:\Users\administrator.EZER-AMIR\Documents\Odmon\Odmon"

dotnet ef database update --context IntegrationDbContext --project Odmon.Worker.csproj --startup-project Odmon.Worker.csproj
```

Ensure the app’s config (e.g. `appsettings.json` or environment) points the Integration Db at the **same** database you want to update (e.g. OdmonIntegration). The tool uses the same config as the running app (startup project).

## Verify Nispah tables exist

After `database update`, confirm the tables are present:

```sql
SELECT name FROM sys.tables WHERE name IN (N'NispahDeduplications', N'NispahAuditLogs') ORDER BY name;
```

Expected: two rows — `NispahAuditLogs`, `NispahDeduplications`.

Runtime: DocumentIngestionWorker logs at startup either  
`HEALTHCHECK | NispahDeduplications table exists in IntegrationDb — dedup is active.`  
or  
`HEALTHCHECK | NispahDeduplications table NOT FOUND in IntegrationDb (SqlException 208)...`  
if the table is missing.

**Dedup idempotency:** Duplicate-key violations (SQL 2601/2627) when inserting into `NispahDeduplications` are treated as a normal skip (no exception, no critical alert). See [NISPAH_DEDUP_AND_INCIDENT_ALERTS.md](NISPAH_DEDUP_AND_INCIDENT_ALERTS.md) for behavior, incident classification, and accident story flow.

## Migration: AddNispahDedupAndAuditTables

- **Migration name:** `20260220112714_AddNispahDedupAndAuditTables`
- **Purpose:** Idempotent creation of `NispahAuditLogs` and `NispahDeduplications` if they are missing (e.g. migration history out of sync with the DB).
- **Safe to run:** Yes. Uses `IF NOT EXISTS`; no-op when tables already exist.

## Add a new migration (for future changes)

```powershell
cd "C:\Users\administrator.EZER-AMIR\Documents\Odmon\Odmon"

dotnet ef migrations add YourMigrationName --context IntegrationDbContext --project Odmon.Worker.csproj --startup-project Odmon.Worker.csproj
```

Then apply with `dotnet ef database update` as above.
