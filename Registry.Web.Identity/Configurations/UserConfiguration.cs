using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Registry.Web.Identity.Models;

namespace Registry.Web.Identity.Configurations;

/// <summary>The stored form of <see cref="User.Metadata"/> and <see cref="User.Preferences"/> (<see cref="UserMetadataJson"/>).</summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        AsJson(builder.Property(b => b.Metadata));
        AsJson(builder.Property(b => b.Preferences));
    }

    // One mapping for every free-form dictionary column, so two of them cannot be stored differently
    private static void AsJson(PropertyBuilder<Dictionary<string, object>> property)
    {
        // By content: the snapshot EF keeps is a deep copy, so a change inside a list or a nested dictionary is seen, and
        // an equal value in another instance is not a change
        var valueComparer = new ValueComparer<Dictionary<string, object>>(
            (x, y) => UserMetadataJson.AreEqual(x!, y!),
            x => UserMetadataJson.GetHashCode(x),
            x => UserMetadataJson.Clone(x));

        property.HasConversion(
            v => UserMetadataJson.Serialize(v),
            v => UserMetadataJson.Deserialize(v) ?? new Dictionary<string, object>(),
            valueComparer);
    }
}
