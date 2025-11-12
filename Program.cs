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
    if (!context.HostingEnvironment.IsDevelopment())
    {
        var vaultUrl = builtConfig["KeyVault:VaultUrl"];
        if (!string.IsNullOrWhiteSpace(vaultUrl))
        {
            configBuilder.AddAzureKeyVault(new Uri(vaultUrl), new DefaultAzureCredential());
        }
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

    if (!env.IsDevelopment())
    {
        var vaultUrl = config["KeyVault:VaultUrl"];
        if (!string.IsNullOrWhiteSpace(vaultUrl))
        {
            services.AddSingleton(new SecretClient(new Uri(vaultUrl), new DefaultAzureCredential()));
            services.AddSingleton<AzureKeyVaultSecretProvider>();
        }
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

    services.AddScoped<IOdcanitReader>(provider =>
    {
        var hostingEnv = provider.GetRequiredService<IHostEnvironment>();
        if (hostingEnv.IsDevelopment())
        {
            return provider.GetRequiredService<LocalDbOdcanitReader>();
        }

        return provider.GetRequiredService<SqlOdcanitReader>();
    });
    services.AddScoped<LocalDbOdcanitReader>();

    if (!env.IsDevelopment())
    {
        services.AddDbContext<OdcanitDbContext>((sp, options) =>
        {
            var connectionString = ResolveConnectionString(sp, "OdcanitDb__ConnectionString", "OdcanitDb", required: true);
            options.UseSqlServer(connectionString);
        });
        services.AddScoped<SqlOdcanitReader>();
        services.AddScoped<IOdcanitChangeFeed, SqlOdcanitChangeFeed>();
    }

    services.AddHttpClient<IMondayClient, MondayClient>(client =>
    {
        client.BaseAddress = new Uri("https://api.monday.com/v2/");
    });

    services.AddScoped<SyncService>();
    services.AddHostedService<SyncWorker>();
});

var host = hostBuilder.Build();

await ValidateRequiredSecretsAsync(host.Services);

await host.RunAsync();

static string ResolveConnectionString(IServiceProvider serviceProvider, string secretKey, string connectionName, bool required)
{
    var secretProvider = serviceProvider.GetRequiredService<ISecretProvider>();
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ConnectionStrings");

    var secretValue = secretProvider.GetSecret(secretKey);
    if (!string.IsNullOrWhiteSpace(secretValue))
    {
        return secretValue;
    }

    var fallback = configuration.GetConnectionString(connectionName);
    if (!string.IsNullOrWhiteSpace(fallback))
    {
        logger.LogWarning("Connection string '{ConnectionName}' retrieved from configuration fallback. Move it to secret key '{SecretKey}'.", connectionName, secretKey);
        return fallback;
    }

    if (required)
    {
        throw new InvalidOperationException($"Connection string '{connectionName}' is not configured. Provide it via secret '{secretKey}'.");
    }

    logger.LogInformation("Connection string '{ConnectionName}' not configured.", connectionName);
    return string.Empty;
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

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            logger.LogWarning("Secret '{SecretKey}' not found in secret providers; using configuration fallback temporarily.", key);
            continue;
        }

        throw new InvalidOperationException($"Required secret '{key}' was not found. See README for configuration steps.");
    }
}
