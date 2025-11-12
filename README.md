# Odmon Worker Service

A .NET 8 Worker Service that synchronizes data between Odcanit and Monday.com.

## Project Structure

- **Models/**: Data models (OdcanitCase, OdcanitUser, MondayItemMapping, SyncLog)
- **Data/**: IntegrationDbContext for storing sync mappings and logs
- **OdcanitAccess/**: Database context and readers for Odcanit system
- **Monday/**: Monday.com API client
- **Services/**: SyncService for handling synchronization logic
- **Workers/**: Background worker service

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

## Running the Service

```bash
dotnet run
```

Or build and run:

```bash
dotnet build
dotnet run
```

