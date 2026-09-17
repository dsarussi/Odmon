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

namespace Odmon.Worker.Services;

internal readonly record struct HearingStatusRepairCliRequest(HearingStatusRepairMode Mode);

internal static class HearingStatusRepairCli
{
    internal const string CommandOption = "--hearing-status-repair";
    internal const string ModeOption = "--mode";
    internal const string ConfirmLiveOption = "--confirm-live";
    internal const string LiveConfirmation = "STATUS-ONLY-49";

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out HearingStatusRepairCliRequest request)
    {
        var commandCount = 0;
        var modeCount = 0;
        var confirmCount = 0;
        string? rawMode = null;
        string? rawConfirmation = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, CommandOption, StringComparison.OrdinalIgnoreCase))
            {
                commandCount++;
            }
            else if (argument.StartsWith(CommandOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidInvocation();
            }
            else if (string.Equals(argument, ModeOption, StringComparison.OrdinalIgnoreCase))
            {
                modeCount++;
                if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw InvalidInvocation();
                }
                rawMode = arguments[++index];
            }
            else if (argument.StartsWith(ModeOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                modeCount++;
                rawMode = argument[(ModeOption.Length + 1)..];
            }
            else if (string.Equals(argument, ConfirmLiveOption, StringComparison.OrdinalIgnoreCase))
            {
                confirmCount++;
                if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw InvalidInvocation();
                }
                rawConfirmation = arguments[++index];
            }
            else if (argument.StartsWith(ConfirmLiveOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                confirmCount++;
                rawConfirmation = argument[(ConfirmLiveOption.Length + 1)..];
            }
        }

        if (commandCount == 0)
        {
            request = default;
            return false;
        }

        if (commandCount != 1 || modeCount != 1 ||
            !Enum.TryParse<HearingStatusRepairMode>(rawMode, ignoreCase: true, out var mode) ||
            !Enum.IsDefined(mode))
        {
            throw InvalidInvocation();
        }

        if (mode == HearingStatusRepairMode.Live)
        {
            if (confirmCount != 1 || !string.Equals(rawConfirmation, LiveConfirmation, StringComparison.Ordinal))
            {
                throw InvalidInvocation();
            }
        }
        else if (confirmCount != 0)
        {
            throw InvalidInvocation();
        }

        request = new HearingStatusRepairCliRequest(mode);
        return true;
    }

    public static async Task RunAsync(
        string[] arguments,
        HearingStatusRepairCliRequest request,
        CancellationToken ct)
    {
        try
        {
            using var host = BuildIsolatedHost(arguments);
            await using var scope = host.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<HearingStatusRepairService>();
            var summary = await service.RunAsync(request.Mode, ct);
            PrintSummary(request.Mode, summary, Console.Out);
            Environment.ExitCode = summary.ValidationFailed == 0 &&
                                   summary.MondayFailed == 0 &&
                                   summary.VerificationFailed == 0
                ? 0
                : 2;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("Hearing status repair cancelled.");
            PrintAbortedSummary(request.Mode, Console.Out);
            Environment.ExitCode = 2;
        }
        catch
        {
            Console.Error.WriteLine("Hearing status repair aborted by a global preflight or infrastructure failure.");
            PrintAbortedSummary(request.Mode, Console.Out);
            Environment.ExitCode = 2;
        }
    }

    internal static void PrintSummary(
        HearingStatusRepairMode mode,
        HearingStatusRepairSummary summary,
        TextWriter output)
    {
        var targets = HearingStatusRepairService.GetTargets();
        output.WriteLine($"Mode={mode}");
        output.WriteLine($"Allowlisted={summary.Allowlisted}");
        output.WriteLine($"UniqueAllowlisted={targets.Select(target => target.MondayItemId).Distinct().Count()}");
        output.WriteLine($"ExpectedCancelled={targets.Count(target => target.ExpectedMeetStatus == 1)}");
        output.WriteLine($"ExpectedTransferred={targets.Count(target => target.ExpectedMeetStatus == 2)}");
        output.WriteLine($"SourceValidated={summary.SourceValidated}");
        output.WriteLine($"Planned={summary.Planned}");
        output.WriteLine($"AlreadyCorrect={summary.AlreadyCorrect}");
        output.WriteLine($"Updated={summary.Updated}");
        output.WriteLine($"ValidationFailed={summary.ValidationFailed}");
        output.WriteLine($"MondayFailed={summary.MondayFailed}");
        output.WriteLine($"VerificationFailed={summary.VerificationFailed}");
        output.WriteLine($"SnapshotStatusRecorded={summary.SnapshotStatusRecorded}");
        output.WriteLine($"SnapshotUnchanged={summary.SnapshotUnchanged}");
        foreach (var failure in summary.ValidationFailures)
        {
            output.WriteLine($"ValidationFailure ItemId={failure.MondayItemId} Reason={failure.ReasonCode}");
        }
        foreach (var failure in summary.MondayFailures)
        {
            output.WriteLine($"MondayFailure ItemId={failure.MondayItemId} Reason={failure.ReasonCode}");
        }
        foreach (var failure in summary.VerificationFailures)
        {
            output.WriteLine($"VerificationFailure ItemId={failure.MondayItemId} Reason={failure.ReasonCode}");
        }
    }

    private static void PrintAbortedSummary(HearingStatusRepairMode mode, TextWriter output)
    {
        var targets = HearingStatusRepairService.GetTargets();
        output.WriteLine($"Mode={mode}");
        output.WriteLine($"Allowlisted={targets.Count}");
        output.WriteLine($"UniqueAllowlisted={targets.Select(target => target.MondayItemId).Distinct().Count()}");
        output.WriteLine($"ExpectedCancelled={targets.Count(target => target.ExpectedMeetStatus == 1)}");
        output.WriteLine($"ExpectedTransferred={targets.Count(target => target.ExpectedMeetStatus == 2)}");
        output.WriteLine("SourceValidated=0");
        output.WriteLine("Planned=0");
        output.WriteLine("AlreadyCorrect=0");
        output.WriteLine("Updated=0");
        output.WriteLine($"ValidationFailed={targets.Count}");
        output.WriteLine("MondayFailed=0");
        output.WriteLine("VerificationFailed=0");
        output.WriteLine("SnapshotStatusRecorded=0");
        output.WriteLine($"SnapshotUnchanged={targets.Count}");
        output.WriteLine("GlobalAbort=true");
    }

    internal static IHost BuildIsolatedHost(string[] arguments)
    {
        return Host.CreateDefaultBuilder(GetHostArguments(arguments))
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.SetBasePath(AppContext.BaseDirectory);
                var configuration = configurationBuilder.Build();
                if (IsKeyVaultEnabled(configuration))
                {
                    var vaultUrl = configuration["KeyVault:VaultUrl"] ?? string.Empty;
                    configurationBuilder.AddAzureKeyVault(
                        new Uri(vaultUrl.Trim(), UriKind.Absolute),
                        new DefaultAzureCredential());
                }
            })
            .ConfigureServices((context, services) =>
            {
                RegisterSecretProviders(context, services);
                services.AddDbContext<IntegrationDbContext>((serviceProvider, options) =>
                    options.UseSqlServer(ResolveConnectionString(
                        serviceProvider,
                        "IntegrationDb__ConnectionString",
                        "IntegrationDb")));
                services.AddDbContext<OdcanitDbContext>((serviceProvider, options) =>
                    options.UseSqlServer(ResolveConnectionString(
                        serviceProvider,
                        "OdcanitDb__ConnectionString",
                        "OdcanitDb")));
                services.AddScoped<MondayMappingReadService>();
                services.AddScoped<IOdcanitReader, SqlOdcanitReader>();
                services.AddHttpClient<IMondayClient, MondayClient>(client =>
                    client.BaseAddress = new Uri("https://api.monday.com/v2/"));
                services.AddHttpClient<IMondayMetadataProvider, MondayMetadataProvider>(client =>
                    client.BaseAddress = new Uri("https://api.monday.com/v2/"));
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IHearingStatusRepairDelay, HearingStatusRepairDelay>();
                services.AddScoped<HearingStatusRepairService>();
            })
            .Build();
    }

    private static string[] GetHostArguments(IReadOnlyList<string> arguments)
    {
        var hostArguments = new List<string>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, CommandOption, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(argument, ModeOption, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, ConfirmLiveOption, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }
            if (argument.StartsWith(ModeOption + "=", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith(ConfirmLiveOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            hostArguments.Add(argument);
        }
        return hostArguments.ToArray();
    }

    private static void RegisterSecretProviders(
        HostBuilderContext context,
        IServiceCollection services)
    {
        services.AddSingleton<EnvironmentSecretProvider>();
        if (context.HostingEnvironment.IsDevelopment())
        {
            services.AddSingleton<UserSecretsProvider>();
        }
        if (IsKeyVaultEnabled(context.Configuration))
        {
            var vaultUrl = context.Configuration["KeyVault:VaultUrl"] ?? string.Empty;
            services.AddSingleton(new SecretClient(
                new Uri(vaultUrl.Trim(), UriKind.Absolute),
                new DefaultAzureCredential()));
            services.AddSingleton<AzureKeyVaultSecretProvider>();
        }
        services.AddSingleton<ISecretProvider>(serviceProvider =>
        {
            var providers = new List<ISecretProvider>();
            if (context.HostingEnvironment.IsDevelopment())
            {
                var userSecrets = serviceProvider.GetService<UserSecretsProvider>();
                if (userSecrets != null)
                {
                    providers.Add(userSecrets);
                }
            }
            var keyVault = serviceProvider.GetService<AzureKeyVaultSecretProvider>();
            if (keyVault != null)
            {
                providers.Add(keyVault);
            }
            providers.Add(serviceProvider.GetRequiredService<EnvironmentSecretProvider>());
            return new CompositeSecretProvider(providers);
        });
    }

    private static string ResolveConnectionString(
        IServiceProvider serviceProvider,
        string secretKey,
        string connectionName)
    {
        var secrets = serviceProvider.GetRequiredService<ISecretProvider>();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var value = secrets.GetSecret(secretKey);
        if (!string.IsNullOrWhiteSpace(value) && !IsPlaceholderValue(value))
        {
            return value;
        }
        var fallback = configuration.GetConnectionString(connectionName);
        if (!string.IsNullOrWhiteSpace(fallback) && !IsPlaceholderValue(fallback))
        {
            return fallback;
        }
        throw new InvalidOperationException("Required repair data source is not configured.");
    }

    private static bool IsKeyVaultEnabled(IConfiguration configuration)
        => bool.TryParse(configuration["KeyVault:Enabled"], out var enabled) && enabled;

    private static bool IsPlaceholderValue(string value)
        => value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("__USE_SECRET__", StringComparison.OrdinalIgnoreCase) ||
           (value.Contains("__", StringComparison.Ordinal) &&
            !value.Contains("Server=", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("Database=", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase));

    private static ArgumentException InvalidInvocation()
        => new(
            $"{CommandOption} requires exactly one explicit {ModeOption} DryRun|Live value; " +
            $"Live also requires {ConfirmLiveOption} {LiveConfirmation}.");
}
