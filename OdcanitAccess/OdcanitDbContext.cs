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
        public DbSet<OdcanitSideDataLink> SideDataLinks => Set<OdcanitSideDataLink>();
        public DbSet<OdcanitDiaryEvent> DiaryEvents => Set<OdcanitDiaryEvent>();
        public DbSet<OdcanitUserData> UserData => Set<OdcanitUserData>();
        public DbSet<OdcanitHozlapMainData> HozlapMainData => Set<OdcanitHozlapMainData>();
        public DbSet<NetCourtDocument> NetCourtDocuments => Set<NetCourtDocument>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OdcanitCase>(entity =>
            {
                entity.ToView("vwExportToOuterSystems_Files")
                    .HasNoKey();
            });

            modelBuilder.Entity<OdcanitUser>()
                .ToView("vwExportToOuterSystems_LoginUsers")
                .HasNoKey();

            modelBuilder.Entity<OdcanitClient>()
                .ToView("vwExportToOuterSystems_Clients")
                .HasNoKey();

            modelBuilder.Entity<OdcanitSide>()
                .ToView("vwExportToOuterSystems_vwSides", "dbo")
                .HasNoKey();

            modelBuilder.Entity<OdcanitSideDataLink>()
                .ToTable("SIDES", "dbo")
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

            modelBuilder.Entity<NetCourtDocument>()
                .ToView("vwNetCourtDocs")
                .HasNoKey();

            base.OnModelCreating(modelBuilder);
        }
    }
}

