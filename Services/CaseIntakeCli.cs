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
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Security;

namespace Odmon.Worker.Services
{
    internal readonly record struct CaseIntakeCliRequest(
        int TikCounter,
        bool DumpPdfText);

    internal static class CaseIntakeCli
    {
        internal const string TikCounterOption = "--case-intake-tik-counter";
        internal const string DumpPdfTextOption = "--dump-pdf-text";

        public static bool TryParse(
            IReadOnlyList<string> arguments,
            out CaseIntakeCliRequest request)
        {
            string? rawValue = null;
            var occurrences = 0;
            var dumpPdfTextOccurrences = 0;

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
                else if (string.Equals(
                             argument,
                             DumpPdfTextOption,
                             StringComparison.OrdinalIgnoreCase))
                {
                    dumpPdfTextOccurrences++;
                }
                else if (argument.StartsWith(
                             DumpPdfTextOption + "=",
                             StringComparison.OrdinalIgnoreCase))
                {
                    throw InvalidDumpPdfTextOption();
                }
            }

            if (occurrences == 0)
            {
                if (dumpPdfTextOccurrences > 0)
                {
                    throw InvalidDumpPdfTextOption();
                }

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

            if (dumpPdfTextOccurrences > 1)
            {
                throw InvalidDumpPdfTextOption();
            }

            request = new CaseIntakeCliRequest(
                tikCounter,
                DumpPdfText: dumpPdfTextOccurrences == 1);
            return true;
        }

        public static async Task RunAsync(
            string[] arguments,
            CaseIntakeCliRequest request,
            CancellationToken ct)
        {
            using var host = BuildReadOnlyHost(arguments);
            await using var scope = host.Services.CreateAsyncScope();
            Console.OutputEncoding = Encoding.UTF8;

            if (request.DumpPdfText)
            {
                await DumpPdfTextAsync(
                    scope.ServiceProvider.GetRequiredService<ICaseIntakeDocumentReader>(),
                    scope.ServiceProvider.GetRequiredService<IPdfTextExtractor>(),
                    request.TikCounter,
                    Console.Out,
                    ct);
                return;
            }

            var intakeReader = scope.ServiceProvider.GetRequiredService<CaseIntakeReadService>();
            var result = await intakeReader.ReadAsync(request.TikCounter, ct);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            options.Converters.Add(new JsonStringEnumConverter());
            Console.WriteLine(JsonSerializer.Serialize(result, options));
        }

        internal static async Task DumpPdfTextAsync(
            ICaseIntakeDocumentReader documentReader,
            IPdfTextExtractor pdfTextExtractor,
            int tikCounter,
            TextWriter output,
            CancellationToken ct)
        {
            var documents = await documentReader.GetRelevantDocumentsAsync(tikCounter, ct);
            var isFirstDocument = true;

            foreach (var document in documents)
            {
                ct.ThrowIfCancellationRequested();
                if (!CaseIntakeDocumentClassifier.TryClassify(
                        document.Name,
                        out _,
                        out _))
                {
                    continue;
                }

                var text = await pdfTextExtractor.ExtractTextAsync(
                    document.Path ?? string.Empty,
                    ct);

                if (!isFirstDocument)
                {
                    await output.WriteLineAsync();
                }

                await output.WriteLineAsync($"Document ID: {document.Id}");
                await output.WriteLineAsync($"Document name: {document.Name}");
                await output.WriteLineAsync("BEGIN EXTRACTED TEXT");
                await output.WriteAsync(text);
                if (text.Length == 0 || (text[^1] != '\r' && text[^1] != '\n'))
                {
                    await output.WriteLineAsync();
                }

                await output.WriteLineAsync("END EXTRACTED TEXT");
                isFirstDocument = false;
            }
        }

        internal static IHost BuildReadOnlyHost(string[] arguments)
        {
            var builder = Host.CreateDefaultBuilder(GetHostArguments(arguments))
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

        private static string[] GetHostArguments(IReadOnlyList<string> arguments)
        {
            var hostArguments = new List<string>(arguments.Count);
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index];
                if (string.Equals(argument, TikCounterOption, StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    continue;
                }

                if (argument.StartsWith(
                        TikCounterOption + "=",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        argument,
                        DumpPdfTextOption,
                        StringComparison.OrdinalIgnoreCase))
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

        private static ArgumentException InvalidDumpPdfTextOption()
            => new(
                $"{DumpPdfTextOption} may be specified once and requires {TikCounterOption}.");
    }
}
