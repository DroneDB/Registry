using Microsoft.EntityFrameworkCore;
using Registry.Web.Data.Models;

namespace Registry.Web.Data;

public class RegistryContext : DbContext
{
    public RegistryContext(DbContextOptions<RegistryContext> options)
        : base(options)
    {
    }

    /// <summary>For test doubles of the context (a mock stands in for it where no query is made); never used to query.</summary>
    protected RegistryContext()
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RegistryContext).Assembly);

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