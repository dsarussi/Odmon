using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Odmon.Worker.Data;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public class TestSafetyPolicy : ITestSafetyPolicy
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;

        public TestSafetyPolicy(IServiceScopeFactory scopeFactory, IConfiguration config)
        {
            _scopeFactory = scopeFactory;
            _config = config;
        }

        public bool IsTestCase(OdcanitCase c)
        {
            if (c == null)
            {
                return false;
            }

            var safetySection = _config.GetSection("Safety");
            var testMode = safetySection.GetValue<bool>("TestMode", false);
            if (!testMode)
            {
                return true;
            }

            var namePrefix = safetySection["AllowedTikNamePrefix"];
            if (!string.IsNullOrWhiteSpace(namePrefix) &&
                !string.IsNullOrWhiteSpace(c.TikName) &&
                c.TikName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var tikNumberPrefixes = safetySection.GetSection("AllowedTikNumberPrefixes").Get<string[]>() ?? Array.Empty<string>();
            if (!string.IsNullOrWhiteSpace(c.TikNumber) &&
                tikNumberPrefixes.Any(p => !string.IsNullOrWhiteSpace(p) &&
                                           c.TikNumber.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var allowedTikCounters = safetySection.GetSection("AllowedTikCounters").Get<int[]>() ?? Array.Empty<int>();
            if (allowedTikCounters.Contains(c.TikCounter))
            {
                return true;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

            return db.AllowedTiks
                .AsNoTracking()
                .Any(t => t.TikCounter == c.TikCounter);
        }
    }
}

