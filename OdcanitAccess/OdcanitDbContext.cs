using Microsoft.EntityFrameworkCore;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public class OdcanitDbContext : DbContext
    {
        public OdcanitDbContext(DbContextOptions<OdcanitDbContext> options) : base(options) { }

        public DbSet<OdcanitCase> Cases => Set<OdcanitCase>();
        public DbSet<OdcanitUser> Users => Set<OdcanitUser>();
        public DbSet<OdcanitClient> Clients => Set<OdcanitClient>();
        public DbSet<OdcanitSide> Sides => Set<OdcanitSide>();
        public DbSet<OdcanitDiaryEvent> DiaryEvents => Set<OdcanitDiaryEvent>();
        public DbSet<OdcanitUserData> UserData => Set<OdcanitUserData>();
        public DbSet<OdcanitHozlapMainData> HozlapMainData => Set<OdcanitHozlapMainData>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OdcanitCase>(entity =>
            {
                entity.ToView("vwExportToOuterSystems_Files")
                    .HasNoKey();

                // Explicitly configure decimal precision for monetary values to avoid silent truncation
                // Even though RequestedClaimAmount is [NotMapped] (populated from user data, not the view),
                // EF Core still validates decimal properties during model building and requires explicit precision
                // to prevent silent truncation warnings. Using decimal(18,2) which is standard for monetary values.
                entity.Property(e => e.RequestedClaimAmount)
                    .HasPrecision(18, 2);
            });

            modelBuilder.Entity<OdcanitUser>()
                .ToView("vwExportToOuterSystems_LoginUsers")
                .HasNoKey();

            modelBuilder.Entity<OdcanitClient>()
                .ToView("vwExportToOuterSystems_Clients")
                .HasNoKey();

            modelBuilder.Entity<OdcanitSide>()
                .ToView("vwExportToOuterSystems_vwSides")
                .HasNoKey();

            modelBuilder.Entity<OdcanitDiaryEvent>()
                .ToView("vwExportToOuterSystems_YomanData")
                .HasNoKey();

            modelBuilder.Entity<OdcanitUserData>()
                .ToView("vwExportToOuterSystems_UserData")
                .HasNoKey();

            modelBuilder.Entity<OdcanitHozlapMainData>()
                .ToView("vwHozlapFormsData_TikMainData")
                .HasNoKey();

            base.OnModelCreating(modelBuilder);
        }
    }
}

