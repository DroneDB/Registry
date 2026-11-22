using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Registry.Web.Data.Models;

namespace Registry.Web.Data.Configurations;

/// <summary>The key, the concurrency token and the organization relationship of <see cref="OrganizationReportSettings"/>.</summary>
internal sealed class OrganizationReportSettingsConfiguration : IEntityTypeConfiguration<OrganizationReportSettings>
{
    public void Configure(EntityTypeBuilder<OrganizationReportSettings> e)
    {
        e.HasKey(x => x.OrganizationSlug);

        // The facet of Organization.Slug
        e.Property(x => x.OrganizationSlug).HasMaxLength(128);

        // The texts are JSON of a size the manager bounds (the limits of the templates and of the schema)
        e.Property(x => x.Templates).IsRequired();
        e.Property(x => x.Schema).IsRequired();
        e.Property(x => x.UpdatedBy).HasMaxLength(256).IsRequired();

        // The version the writer read must still be the stored one: the update is "WHERE Version = <read>"
        e.Property(x => x.Version).IsConcurrencyToken();

        e.HasOne<Organization>()
            .WithOne()
            .HasForeignKey<OrganizationReportSettings>(x => x.OrganizationSlug)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
