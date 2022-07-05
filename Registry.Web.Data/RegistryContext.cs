using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Registry.Web.Data.Models;

namespace Registry.Web.Data
{
    public class RegistryContext : DbContext
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

            var valueComparer = new ValueComparer<string[]>(
                (c1, c2) => c1.SequenceEqual(c2),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToArray());

            modelBuilder.Entity<Dataset>()
                .Property(e => e.FileTypes)
                .HasConversion(
                    v => string.Join(';', v),
                    v => v.Split(';', StringSplitOptions.RemoveEmptyEntries))
                .Metadata
                .SetValueComparer(valueComparer);

            modelBuilder.Entity<OrganizationUser>()
                .HasKey(c => new { c.OrganizationSlug, c.UserId });

            modelBuilder
                .Entity<OrganizationUser>()
                .HasOne(ds => ds.Organization)
                .WithMany(org => org.Users)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
#if DEBUG
            optionsBuilder.EnableSensitiveDataLogging();
#else
            optionsBuilder.EnableSensitiveDataLogging(false);
#endif

            optionsBuilder.EnableDetailedErrors();
        }

        public DbSet<Organization> Organizations { get; set; }
        public DbSet<OrganizationUser> OrganizationsUsers { get; set; }
        public DbSet<Dataset> Datasets { get; set; }
        public DbSet<Batch> Batches { get; set; }
    }
}