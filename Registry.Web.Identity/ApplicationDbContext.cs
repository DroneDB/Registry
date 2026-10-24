using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Identity.Models;

namespace Registry.Web.Identity;

public sealed class ApplicationDbContext : IdentityDbContext<User>
{
    public const string AdminRoleName = "admin";
    public const string DeactivatedRoleName = "deactivated";

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

#if DEBUG
        optionsBuilder.EnableSensitiveDataLogging();
#else
            optionsBuilder.EnableSensitiveDataLogging(false);
#endif

        optionsBuilder.EnableDetailedErrors();
    }
}