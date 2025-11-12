using Microsoft.EntityFrameworkCore;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public class OdcanitDbContext : DbContext
    {
        public OdcanitDbContext(DbContextOptions<OdcanitDbContext> options) : base(options) { }

        public DbSet<OdcanitCase> Cases => Set<OdcanitCase>();
        public DbSet<OdcanitUser> Users => Set<OdcanitUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OdcanitCase>()
                .ToView("vwExportToOuterSystems_Files")
                .HasNoKey();

            modelBuilder.Entity<OdcanitUser>()
                .ToView("vwExportToOuterSystems_LoginUsers")
                .HasNoKey();

            base.OnModelCreating(modelBuilder);
        }
    }
}

