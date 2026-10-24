using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Registry.Web.Data.Models;

namespace Registry.Web.Data.Configurations;

/// <summary>Indexes of <see cref="Organization"/> (its slug is the primary key).</summary>
internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.HasIndex(d => d.Slug);

        // For queries filtering by Organization.OwnerId (used frequently)
        builder.HasIndex(o => o.OwnerId);
    }
}
