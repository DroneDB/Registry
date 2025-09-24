using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Registry.Web.Data.Models;

namespace Registry.Web.Data;

public class RegistryContext : DbContext
{
    public RegistryContext(DbContextOptions<RegistryContext> options)
        : base(options)
    {
    }

    public RegistryContext()
    {
        // Required for migrations
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Dataset>()
            .HasIndex(ds => ds.Slug);

        modelBuilder.Entity<Organization>()
            .HasIndex(d => d.Slug);

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
        
        modelBuilder.Entity<JobIndex>(e =>
        {
            e.HasKey(x => x.JobId);
            e.Property(x => x.JobId).HasMaxLength(64);
            e.Property(x => x.OrgSlug).HasMaxLength(128).IsRequired();
            e.Property(x => x.DsSlug).HasMaxLength(128).IsRequired();
            e.Property(x => x.Path).HasMaxLength(1024);
            e.Property(x => x.UserId).HasMaxLength(128);
            e.Property(x => x.Queue).HasMaxLength(64);
            e.Property(x => x.CurrentState).HasMaxLength(32).IsRequired();
            e.Property(x => x.MethodDisplay).HasMaxLength(1024);
            
            e.HasIndex(x => new { x.OrgSlug, x.DsSlug });
            e.HasIndex(x => new { x.OrgSlug, x.DsSlug, x.Path });
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.CreatedAtUtc);
        });
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
    
    public DbSet<JobIndex> JobIndices => Set<JobIndex>();

}