using Microsoft.EntityFrameworkCore;
using Odmon.Worker.Models;

namespace Odmon.Worker.Data
{
    public class IntegrationDbContext : DbContext
    {
        public IntegrationDbContext(DbContextOptions<IntegrationDbContext> options) : base(options) { }

        public DbSet<MondayItemMapping> MondayItemMappings => Set<MondayItemMapping>();
        public DbSet<SyncLog> SyncLogs => Set<SyncLog>();
        public DbSet<OdcanitCase> OdcanitMockCases => Set<OdcanitCase>();
        public DbSet<AllowedTik> AllowedTiks => Set<AllowedTik>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MondayItemMapping>()
                .HasKey(m => m.Id);
            modelBuilder.Entity<MondayItemMapping>()
                .HasIndex(m => m.TikCounter)
                .IsUnique();
            modelBuilder.Entity<MondayItemMapping>()
                .HasIndex(m => m.MondayItemId);

            modelBuilder.Entity<SyncLog>()
                .HasKey(l => l.Id);

            modelBuilder.Entity<AllowedTik>(b =>
            {
                b.ToTable("AllowedTik");
                b.HasKey(x => x.TikCounter);
            });

            modelBuilder.Entity<OdcanitCase>(b =>
            {
                b.HasNoKey();
                b.ToView(null);
                b.Property(x => x.TikCounter);
                b.Property(x => x.TikNumber);
                b.Property(x => x.TikName);
                b.Property(x => x.ClientName);
                b.Property(x => x.StatusName);
                b.Property(x => x.TikOwner);
                b.Property(x => x.tsCreateDate);
                b.Property(x => x.tsModifyDate);
                b.Property(x => x.Notes);
                b.Property(x => x.ClientVisualID);
                b.Property(x => x.HozlapTikNumber);
                b.Property(x => x.ClientPhone);
                b.Property(x => x.ClientEmail);
                b.Property(x => x.EventDate);
                b.Property(x => x.ClaimAmount);
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}
