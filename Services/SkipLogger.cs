using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Data;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Inserts skip events into OdmonIntegration.dbo.SkipEvents.
    /// Any failure is logged but does not affect the main run.
    /// </summary>
    public class SkipLogger : ISkipLogger
    {
        private readonly IntegrationDbContext _integrationDb;
        private readonly ILogger<SkipLogger> _logger;

        public SkipLogger(IntegrationDbContext integrationDb, ILogger<SkipLogger> logger)
        {
            _integrationDb = integrationDb;
            _logger = logger;
        }

        public async Task LogSkipAsync(
            int tikCounter,
            string? tikNumber,
            string operation,
            string reasonCode,
            string? entityId,
            string? rawValue,
            object? details,
            CancellationToken ct)
        {
            try
            {
                var json = details != null
                    ? JsonSerializer.Serialize(details)
                    : null;

                var sql = @"
INSERT INTO dbo.SkipEvents (TikCounter, TikNumber, Operation, ReasonCode, EntityId, RawValue, DetailsJson)
VALUES (@TikCounter, @TikNumber, @Operation, @ReasonCode, @EntityId, @RawValue, @DetailsJson);";

                var parameters = new[]
                {
                    new SqlParameter("@TikCounter", tikCounter),
                    new SqlParameter("@TikNumber", (object?)tikNumber ?? DBNull.Value),
                    new SqlParameter("@Operation", operation ?? string.Empty),
                    new SqlParameter("@ReasonCode", reasonCode ?? string.Empty),
                    new SqlParameter("@EntityId", (object?)entityId ?? DBNull.Value),
                    new SqlParameter("@RawValue", (object?)rawValue ?? DBNull.Value),
                    new SqlParameter("@DetailsJson", (object?)json ?? DBNull.Value)
                };

                await _integrationDb.Database.ExecuteSqlRawAsync(sql, parameters, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to insert SkipEvents row: TikCounter={TikCounter}, TikNumber={TikNumber}, Operation={Operation}, ReasonCode={ReasonCode}, EntityId={EntityId}",
                    tikCounter,
                    tikNumber ?? "<null>",
                    operation,
                    reasonCode,
                    entityId ?? "<null>");
            }
        }
    }
}

