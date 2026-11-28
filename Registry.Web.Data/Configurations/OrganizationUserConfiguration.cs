using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Registry.Web.Data.Models;

namespace Registry.Web.Data.Configurations;

/// <summary>The composite key, the user index and the organization relationship of <see cref="OrganizationUser"/>.</summary>
internal sealed class OrganizationUserConfiguration : IEntityTypeConfiguration<OrganizationUser>
{
    public void Configure(EntityTypeBuilder<OrganizationUser> builder)
    {
        builder.HasKey(c => new { c.OrganizationSlug, c.UserId });

        // For OrganizationUser queries by UserId
        builder.HasIndex(ou => ou.UserId);

        builder
            .HasOne(ds => ds.Organization)
            .WithMany(org => org.Users)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
