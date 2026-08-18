using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class CaseIntakeCliTests
    {
        [Fact]
        public void TryParse_ExactForwardedServerArguments_DetectsReadOnlyCommand()
        {
            var arguments = new[] { "--case-intake-tik-counter", "40514" };

            var detected = CaseIntakeCli.TryParse(arguments, out var request);

            Assert.True(detected);
            Assert.Equal(40514, request.TikCounter);
        }

        [Fact]
        public void TryParse_EqualsSyntax_DetectsReadOnlyCommand()
        {
            var detected = CaseIntakeCli.TryParse(
                ["--case-intake-tik-counter=40514"],
                out var request);

            Assert.True(detected);
            Assert.Equal(40514, request.TikCounter);
        }

        [Fact]
        public void TryParse_NoCaseIntakeOption_LeavesProductionDispatchUntouched()
        {
            var detected = CaseIntakeCli.TryParse(["--environment", "Production"], out _);

            Assert.False(detected);
        }

        public static TheoryData<string[]> InvalidArguments => new()
        {
            new[] { "--case-intake-tik-counter" },
            new[] { "--case-intake-tik-counter", "0" },
            new[] { "--case-intake-tik-counter", "-1" },
            new[] { "--case-intake-tik-counter", "not-a-number" }
        };

        [Theory]
        [MemberData(nameof(InvalidArguments))]
        public void TryParse_InvalidCaseIntakeOption_FailsClosed(string[] arguments)
        {
            Assert.Throws<ArgumentException>(() => CaseIntakeCli.TryParse(arguments, out _));
        }

        [Fact]
        public void TryParse_DuplicateCaseIntakeOption_FailsClosed()
        {
            var arguments = new[]
            {
                "--case-intake-tik-counter", "40514",
                "--case-intake-tik-counter=40514"
            };

            Assert.Throws<ArgumentException>(() => CaseIntakeCli.TryParse(arguments, out _));
        }

        [Fact]
        public void ReadOnlyHost_ContainsNoHostedServicesOrWriteIntegrations()
        {
            using var host = CaseIntakeCli.BuildReadOnlyHost(
                [
                    "--case-intake-tik-counter", "40514",
                    "--ConnectionStrings:OdcanitDb", "Server=localhost;Database=Odcanit;Integrated Security=true"
                ]);

            Assert.Empty(host.Services.GetServices<IHostedService>());
            Assert.Null(host.Services.GetService<IOdcanitWriter>());
            Assert.Null(host.Services.GetService<IMondayClient>());
            Assert.Null(host.Services.GetService<IntegrationDbContext>());

            using var scope = host.Services.CreateScope();
            Assert.NotNull(scope.ServiceProvider.GetService<CaseIntakeReadService>());
        }
    }
}
