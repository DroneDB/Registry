using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Registry.Common;
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

        var valueComparer = new ValueComparer<Dictionary<string, object>>(
            (x, y) => x.IsSameAs(y),
            x => x.GetHashCode(),
            x => UserMetadataJson.Clone(x));

        // TODO: Need to set value comparer
        builder.Entity<User>()
            .Property(b => b.Metadata)
            .HasConversion(
                v => UserMetadataJson.Serialize(v),
                v => UserMetadataJson.Deserialize(v),
                valueComparer);

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