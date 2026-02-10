using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class FixSyncRunLockIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the old table (has IDENTITY on Id)
            migrationBuilder.DropTable(name: "SyncRunLocks");

            // Recreate without IDENTITY
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
            // Revert to IDENTITY version
            migrationBuilder.DropTable(name: "SyncRunLocks");

            migrationBuilder.CreateTable(
                name: "SyncRunLocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LockedByRunId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LockedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunLocks", x => x.Id);
                });
        }
    }
}
