using System;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data.Models;

namespace Registry.Web.Data
{
    public class RegistryContext: DbContext
    {
        public RegistryContext(DbContextOptions<RegistryContext> options)
            : base(options)
        {

        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Dataset>()
                .HasIndex(ds => ds.Slug);

            modelBuilder
                .Entity<Dataset>()
                .HasOne(ds => ds.Organization)
                .WithMany(org => org.Datasets)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder
                .Entity<Batch>()
                .HasOne(e => e.Dataset)
                .WithMany(e => e.Batches)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder
                .Entity<Entry>()
                .HasOne(e => e.Batch)
                .WithMany(e => e.Entries)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder
                .Entity<DownloadPackage>()
                .HasOne(e => e.Dataset)
                .WithMany(e => e.DownloadPackages)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
            
            // TODO: Need to set value comparer
            modelBuilder.Entity<DownloadPackage>()
                .Property(e => e.Paths)
                .HasConversion(
                    v => string.Join(';', v),
                    v => v.Split(';', StringSplitOptions.RemoveEmptyEntries));

        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);

#if DEBUG
            optionsBuilder.EnableSensitiveDataLogging();
#endif
        }

        public DbSet<Organization> Organizations { get; set; }
        public DbSet<Dataset> Datasets { get; set; }

        public DbSet<Batch> Batches { get; set; }

        public DbSet<DownloadPackage> DownloadPackages { get; set; }

    }
}
