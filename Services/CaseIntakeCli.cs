using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Security;

namespace Odmon.Worker.Services
{
    internal readonly record struct CaseIntakeCliRequest(int TikCounter);

    internal static class CaseIntakeCli
    {
        internal const string TikCounterOption = "--case-intake-tik-counter";

        public static bool TryParse(
            IReadOnlyList<string> arguments,
            out CaseIntakeCliRequest request)
        {
            string? rawValue = null;
            var occurrences = 0;

            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index];
                if (string.Equals(argument, TikCounterOption, StringComparison.OrdinalIgnoreCase))
                {
                    occurrences++;
                    if (index + 1 >= arguments.Count ||
                        arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw InvalidOption();
                    }

                    rawValue = arguments[++index];
                }
                else if (argument.StartsWith(
                             TikCounterOption + "=",
                             StringComparison.OrdinalIgnoreCase))
                {
                    occurrences++;
                    rawValue = argument[(TikCounterOption.Length + 1)..];
                }
            }

            if (occurrences == 0)
            {
                request = default;
                return false;
            }

            if (occurrences != 1 ||
                !int.TryParse(
                    rawValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var tikCounter) ||
                tikCounter <= 0)
            {
                throw InvalidOption();
            }

            request = new CaseIntakeCliRequest(tikCounter);
            return true;
        }

        public static async Task RunAsync(
            string[] arguments,
            CaseIntakeCliRequest request,
            CancellationToken ct)
        {
            using var host = BuildReadOnlyHost(arguments);
            await using var scope = host.Services.CreateAsyncScope();
            var intakeReader = scope.ServiceProvider.GetRequiredService<CaseIntakeReadService>();
            var result = await intakeReader.ReadAsync(request.TikCounter, ct);

            Console.OutputEncoding = Encoding.UTF8;
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            options.Converters.Add(new JsonStringEnumConverter());
            Console.WriteLine(JsonSerializer.Serialize(result, options));
        }

        internal static IHost BuildReadOnlyHost(string[] arguments)
        {
            var builder = Host.CreateDefaultBuilder(arguments)
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
                    services.AddDbContext<OdcanitDbContext>((serviceProvider, options) =>
                    {
                        options.UseSqlServer(ResolveOdcanitConnectionString(serviceProvider));
                    });

                    services.AddScoped<ICaseIntakeDocumentReader, SqlCaseIntakeDocumentReader>();
                    services.AddScoped<CaseIntakeReadService>();
                    services.AddSingleton<IPdfTextExtractor, PdfTextExtractor>();
                    services.AddSingleton<DigitalNotificationFormParser>();
                    services.AddSingleton<DemandFormParser>();
                    services.AddSingleton<CaseIntakeResultMerger>();
                });

            return builder.Build();
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

        private static string ResolveOdcanitConnectionString(IServiceProvider serviceProvider)
        {
            var secretProvider = serviceProvider.GetRequiredService<ISecretProvider>();
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var secret = secretProvider.GetSecret("OdcanitDb__ConnectionString");
            if (!string.IsNullOrWhiteSpace(secret) && !IsPlaceholderValue(secret))
            {
                return secret;
            }

            var fallback = configuration.GetConnectionString("OdcanitDb");
            if (!string.IsNullOrWhiteSpace(fallback) && !IsPlaceholderValue(fallback))
            {
                return fallback;
            }

            throw new InvalidOperationException(
                "Odcanit connection string is not configured for the read-only case-intake CLI.");
        }

        private static bool IsKeyVaultEnabled(IConfiguration configuration)
            => bool.TryParse(configuration["KeyVault:Enabled"], out var enabled) && enabled;

        private static bool IsPlaceholderValue(string value)
            => string.IsNullOrWhiteSpace(value) ||
               value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("__USE_SECRET__", StringComparison.OrdinalIgnoreCase) ||
               (value.Contains("__", StringComparison.Ordinal) &&
                !value.Contains("Server=", StringComparison.OrdinalIgnoreCase) &&
                !value.Contains("Database=", StringComparison.OrdinalIgnoreCase) &&
                !value.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) &&
                !value.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase));

        private static ArgumentException InvalidOption()
            => new($"{TikCounterOption} requires exactly one positive integer value.");
    }
}
