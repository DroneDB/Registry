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

        // For queries filtering by Organization.OwnerId (used frequently)
        modelBuilder.Entity<Organization>()
            .HasIndex(o => o.OwnerId);

        // For dataset queries by InternalRef (used in GetUserStorage)
        modelBuilder.Entity<Dataset>()
            .HasIndex(ds => ds.InternalRef);

        // For dataset queries by creation date (for sorting/filtering)
        modelBuilder.Entity<Dataset>()
            .HasIndex(ds => ds.CreationDate);

        // For batch queries by status
        modelBuilder.Entity<Batch>()
            .HasIndex(b => b.Status);

        // For batch queries by user and status
        modelBuilder.Entity<Batch>()
            .HasIndex(b => new { b.UserName, b.Status });

        // For batch queries by start date
        modelBuilder.Entity<Batch>()
            .HasIndex(b => b.Start);

        // For OrganizationUser queries by UserId
        modelBuilder.Entity<OrganizationUser>()
            .HasIndex(ou => ou.UserId);

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
            e.Property(x => x.Hash).HasMaxLength(1024);
            e.Property(x => x.Path).HasMaxLength(2048);
            e.Property(x => x.UserId).HasMaxLength(128);
            e.Property(x => x.Queue).HasMaxLength(64);
            e.Property(x => x.CurrentState).HasMaxLength(32).IsRequired();
            e.Property(x => x.MethodDisplay).HasMaxLength(1024);

            // Processing Platform (Layer 1) columns
            e.Property(x => x.ToolId).HasMaxLength(64).IsRequired().HasDefaultValue("build");
            e.Property(x => x.ToolVersion).HasMaxLength(16).IsRequired().HasDefaultValue("1");
            e.Property(x => x.PhaseMessage).HasMaxLength(256);
            e.Property(x => x.ArtifactSha256).HasMaxLength(64);
            e.Property(x => x.ErrorType).HasMaxLength(128);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.Property(x => x.ParentJobId).HasMaxLength(36);
            e.Property(x => x.WorkflowExecutionId).HasMaxLength(36);

            e.HasIndex(x => new { x.OrgSlug, x.DsSlug });
            e.HasIndex(x => new { x.OrgSlug, x.DsSlug, x.Hash });
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.CreatedAtUtc);

            // Processing Platform (Layer 1) indexes
            e.HasIndex(x => new { x.Queue, x.OrgSlug, x.DsSlug, x.ToolId, x.CurrentState, x.CreatedAtUtc })
                .HasDatabaseName("IX_JobIndex_Tool_State");
            e.HasIndex(x => new { x.OrgSlug, x.DsSlug, x.ToolId, x.RequestHash })
                .HasDatabaseName("IX_JobIndex_RequestHash");
            e.HasIndex(x => x.WorkflowExecutionId)
                .HasDatabaseName("IX_JobIndex_Workflow");
            e.HasIndex(x => x.ParentJobId)
                .HasDatabaseName("IX_JobIndex_Parent");
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