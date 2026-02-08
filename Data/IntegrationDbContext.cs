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
        public DbSet<MondayHearingApprovalState> MondayHearingApprovalStates => Set<MondayHearingApprovalState>();
        public DbSet<NispahAuditLog> NispahAuditLogs => Set<NispahAuditLog>();
        public DbSet<NispahDeduplication> NispahDeduplications => Set<NispahDeduplication>();
        public DbSet<HearingNearestSnapshot> HearingNearestSnapshots => Set<HearingNearestSnapshot>();
        public DbSet<SyncFailure> SyncFailures => Set<SyncFailure>();
        public DbSet<SyncRunLock> SyncRunLocks => Set<SyncRunLock>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MondayItemMapping>()
                .HasKey(m => m.Id);
            modelBuilder.Entity<MondayItemMapping>()
                .HasIndex(m => m.TikCounter)
                .IsUnique();
            modelBuilder.Entity<MondayItemMapping>()
                .HasIndex(m => m.MondayItemId);
            modelBuilder.Entity<MondayItemMapping>()
                .HasIndex(m => new { m.TikNumber, m.BoardId });
            modelBuilder.Entity<MondayItemMapping>()
                .Property(m => m.HearingChecksum)
                .HasMaxLength(128);

            modelBuilder.Entity<SyncLog>()
                .HasKey(l => l.Id);

            modelBuilder.Entity<AllowedTik>(b =>
            {
                b.ToTable("AllowedTik");
                b.HasKey(x => x.TikCounter);
                
                // TikCounter is NOT an identity / auto-generated column
                b.Property(x => x.TikCounter)
                 .ValueGeneratedNever();
            });

            modelBuilder.Entity<MondayHearingApprovalState>(b =>
            {
                b.ToTable("MondayHearingApprovalStates");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.BoardId, x.MondayItemId }).IsUnique();
            });

            modelBuilder.Entity<NispahAuditLog>(b =>
            {
                b.ToTable("NispahAuditLogs");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.CorrelationId);
                b.HasIndex(x => new { x.TikVisualID, x.CreatedAtUtc });
            });

            modelBuilder.Entity<NispahDeduplication>(b =>
            {
                b.ToTable("NispahDeduplications");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.TikVisualID, x.NispahTypeName, x.InfoHash }).IsUnique();
                b.HasIndex(x => x.CreatedAtUtc);
            });

            modelBuilder.Entity<HearingNearestSnapshot>(b =>
            {
                b.ToTable("HearingNearestSnapshots");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.TikCounter, x.BoardId }).IsUnique();
                b.HasIndex(x => x.MondayItemId);
            });

            modelBuilder.Entity<SyncFailure>(b =>
            {
                b.ToTable("SyncFailures");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.TikCounter, x.OccurredAtUtc });
                b.HasIndex(x => x.Resolved);
                b.HasIndex(x => x.RunId);
                b.Property(x => x.ErrorMessage).HasMaxLength(2000);
                b.Property(x => x.ErrorType).HasMaxLength(256);
                b.Property(x => x.Operation).HasMaxLength(128);
                b.Property(x => x.RunId).HasMaxLength(64);
                b.Property(x => x.StackTrace).HasMaxLength(4000);
            });

            modelBuilder.Entity<SyncRunLock>(b =>
            {
                b.ToTable("SyncRunLocks");
                b.HasKey(x => x.Id);
                b.Property(x => x.LockedByRunId).HasMaxLength(64);
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
                b.Property(x => x.RequestedClaimAmount);
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}
