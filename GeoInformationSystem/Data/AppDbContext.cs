using GeoAdminDemo.Data.QueryTypes;
using GeoAdminDemo.Models;
using Microsoft.EntityFrameworkCore;

namespace GeoAdminDemo.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AdminArea> AdminAreas => Set<AdminArea>();
    public DbSet<AdminAreaCteRow> AdminAreaCteRows => Set<AdminAreaCteRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AdminArea>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasIndex(x => new { x.CountryIso3, x.Level, x.Code }).IsUnique();
            e.HasIndex(x => new { x.CountryIso3, x.Level, x.ParentId });

            e.Property(x => x.CountryIso3).HasMaxLength(3);
            e.Property(x => x.Code).HasMaxLength(512);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.LevelLabel).HasMaxLength(1024);
            e.Property(x => x.Source).HasMaxLength(128);

            // ✅ Spatial
            e.Property(x => x.Geometry)
                .HasColumnType("geography");

            e.HasOne(x => x.Parent)
             .WithMany(x => x.Children)
             .HasForeignKey(x => x.ParentId)
             .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AdminAreaCteRow>().HasNoKey();
        });
    }
}
