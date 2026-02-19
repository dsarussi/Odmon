using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRunLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncRunLocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    LockedByRunId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LockedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunLocks", x => x.Id);
                });

            // Seed the singleton lock row
            migrationBuilder.InsertData(
                table: "SyncRunLocks",
                columns: new[] { "Id", "LockedByRunId", "LockedAtUtc", "ExpiresAtUtc" },
                values: new object[] { 1, null!, null!, null! });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncRunLocks");
        }
    }
}
