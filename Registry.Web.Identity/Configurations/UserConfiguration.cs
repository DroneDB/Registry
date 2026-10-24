using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Registry.Common;
using Registry.Web.Identity.Models;

namespace Registry.Web.Identity.Configurations;

/// <summary>The stored form of <see cref="User.Metadata"/> (<see cref="UserMetadataJson"/>).</summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        var valueComparer = new ValueComparer<Dictionary<string, object>>(
            (x, y) => x.IsSameAs(y),
            x => x.GetHashCode(),
            x => UserMetadataJson.Clone(x));

        // TODO: Need to set value comparer
        builder
            .Property(b => b.Metadata)
            .HasConversion(
                v => UserMetadataJson.Serialize(v),
                v => UserMetadataJson.Deserialize(v) ?? new Dictionary<string, object>(),
                valueComparer);
    }
}
