using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Security;
using Odmon.Worker.Services;
using Odmon.Worker.Workers;

var hostBuilder = Host.CreateDefaultBuilder(args);

hostBuilder.ConfigureAppConfiguration((context, configBuilder) =>
{
    var builtConfig = configBuilder.Build();
    if (IsKeyVaultEnabled(builtConfig))
    {
        var vaultUrl = builtConfig["KeyVault:VaultUrl"] ?? string.Empty;
        configBuilder.AddAzureKeyVault(new Uri(vaultUrl.Trim(), UriKind.Absolute), new DefaultAzureCredential());
    }
});

hostBuilder.ConfigureServices((context, services) =>
{
    var config = context.Configuration;
    var env = context.HostingEnvironment;

    services.AddSingleton<EnvironmentSecretProvider>();
    if (env.IsDevelopment())
    {
        services.AddSingleton<UserSecretsProvider>();
    }

    if (IsKeyVaultEnabled(config))
    {
        var vaultUrl = config["KeyVault:VaultUrl"] ?? string.Empty;
        services.AddSingleton(new SecretClient(new Uri(vaultUrl.Trim(), UriKind.Absolute), new DefaultAzureCredential()));
        services.AddSingleton<AzureKeyVaultSecretProvider>();
    }

    services.AddSingleton<ISecretProvider>(sp =>
    {
        var orderedProviders = new List<ISecretProvider>();
        if (env.IsDevelopment())
        {
            var user = sp.GetService<UserSecretsProvider>();
            if (user != null)
            {
                orderedProviders.Add(user);
            }
        }

        var keyVaultProvider = sp.GetService<AzureKeyVaultSecretProvider>();
        if (keyVaultProvider != null)
        {
            orderedProviders.Add(keyVaultProvider);
        }

        orderedProviders.Add(sp.GetRequiredService<EnvironmentSecretProvider>());

        return new CompositeSecretProvider(orderedProviders);
    });

    services.AddSingleton<ITestSafetyPolicy, TestSafetyPolicy>();

    services.AddDbContext<IntegrationDbContext>((sp, options) =>
    {
        var connectionString = ResolveConnectionString(sp, "IntegrationDb__ConnectionString", "IntegrationDb", required: true);
        options.UseSqlServer(connectionString);
    });

    services.AddDbContext<OdcanitDbContext>((sp, options) =>
    {
        var connectionString = ResolveConnectionString(sp, "OdcanitDb__ConnectionString", "OdcanitDb", required: true);
        options.UseSqlServer(connectionString);
    });

    services.AddScoped<SqlOdcanitReader>();
    services.AddScoped<GuardOdcanitReader>();
    services.AddScoped<IOdcanitReader>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var testingEnabled = config.GetValue<bool>("Testing:Enable", false);
        if (testingEnabled)
        {
            return sp.GetRequiredService<GuardOdcanitReader>();
        }

        return sp.GetRequiredService<SqlOdcanitReader>();
    });
    services.AddScoped<OdcanitCaseSource>();
    services.AddScoped<IntegrationTestCaseSource>();
    services.AddScoped<ICaseSource>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var testingEnabled = config.GetValue<bool>("Testing:Enable", false);
        if (testingEnabled)
        {
            return sp.GetRequiredService<IntegrationTestCaseSource>();
        }

        return sp.GetRequiredService<OdcanitCaseSource>();
    });

    if (!env.IsDevelopment())
    {
        services.AddScoped<IOdcanitChangeFeed, SqlOdcanitChangeFeed>();
    }

    services.AddScoped<IOdcanitWriter, SqlOdcanitWriter>();
    services.AddScoped<ISkipLogger, SkipLogger>();
    services.AddScoped<HearingApprovalSyncService>();
    services.AddScoped<HearingNearestSyncService>();
    services.AddScoped<TokenResolverService>();
    services.AddScoped<NispahWriterService>();

    services.Configure<MondaySettings>(config.GetSection("Monday"));
    services.Configure<NispahWriterSettings>(config.GetSection("NispahWriter"));

    services.AddHttpClient<IMondayClient, MondayClient>(client =>
    {
        client.BaseAddress = new Uri("https://api.monday.com/v2/");
    });

    services.AddHttpClient<IMondayMetadataProvider, MondayMetadataProvider>(client =>
    {
        client.BaseAddress = new Uri("https://api.monday.com/v2/");
    });

    services.AddScoped<SyncService>();
    services.AddHostedService<SyncWorker>();
});

var host = hostBuilder.Build();

var appConfig = host.Services.GetRequiredService<IConfiguration>();
if (IsKeyVaultEnabled(appConfig))
{
    await ValidateRequiredSecretsAsync(host.Services);
}

await host.RunAsync();

static string ResolveConnectionString(IServiceProvider serviceProvider, string secretKey, string connectionName, bool required)
{
    var secretProvider = serviceProvider.GetRequiredService<ISecretProvider>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ConnectionStrings");

    var secretValue = secretProvider.GetSecret(secretKey);
    if (!string.IsNullOrWhiteSpace(secretValue) && !IsPlaceholderValue(secretValue))
    {
        return secretValue;
    }

    var fallback = configuration.GetConnectionString(connectionName);
    if (!string.IsNullOrWhiteSpace(fallback) && !IsPlaceholderValue(fallback))
    {
        logger.LogWarning("Connection string '{ConnectionName}' retrieved from configuration fallback. Move it to secret key '{SecretKey}'.", connectionName, secretKey);
        return fallback;
    }

    if (required)
    {
        throw new InvalidOperationException($"Connection string '{connectionName}' is not configured. Provide it via secret '{secretKey}' or set it in user-secrets/environment variables. See README for setup instructions.");
    }

    logger.LogInformation("Connection string '{ConnectionName}' not configured.", connectionName);
    return string.Empty;
}

static bool IsKeyVaultEnabled(IConfiguration config)
{
    return bool.TryParse(config["KeyVault:Enabled"], out var enabled) && enabled;
}

static bool IsPlaceholderValue(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return true;
    }

    // Check for common placeholder patterns
    if (value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("__USE_SECRET__", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    // Check if the value looks like a secret key name (contains "__" and doesn't look like a connection string)
    // Secret keys follow the pattern: Something__SomethingElse (e.g., "IntegrationDb__ConnectionString")
    if (value.Contains("__", StringComparison.Ordinal) && 
        !value.Contains("Server=", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("Database=", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase))
    {
        // If it contains "__" and doesn't have connection string keywords, it's likely a secret key reference
        return true;
    }

    return false;
}

static async Task ValidateRequiredSecretsAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var provider = scope.ServiceProvider;
    var secretProvider = provider.GetRequiredService<ISecretProvider>();
    var config = provider.GetRequiredService<IConfiguration>();
    var env = provider.GetRequiredService<IHostEnvironment>();
    var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("StartupSecrets");

    var requiredKeys = new List<string> { "Monday__ApiToken", "IntegrationDb__ConnectionString" };
    if (!env.IsDevelopment())
    {
        requiredKeys.Add("OdcanitDb__ConnectionString");
    }

    foreach (var key in requiredKeys)
    {
        var value = await secretProvider.GetSecretAsync(key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            continue;
        }

        string? fallback = key switch
        {
            "IntegrationDb__ConnectionString" => config.GetConnectionString("IntegrationDb"),
            "OdcanitDb__ConnectionString" => config.GetConnectionString("OdcanitDb"),
            "Monday__ApiToken" => config["Monday:ApiToken"],
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(fallback) && !IsPlaceholderValue(fallback))
        {
            logger.LogWarning("Secret '{SecretKey}' not found in secret providers; using configuration fallback temporarily.", key);
            continue;
        }

        throw new InvalidOperationException($"Required secret '{key}' was not found. Provide it via user-secrets (Development) or Azure KeyVault/Environment Variables (Production). See README for setup instructions.");
    }
}
