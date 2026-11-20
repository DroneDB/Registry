using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Registry.Web.Data.Models;

namespace Registry.Web.Data.Configurations;

/// <summary>Keys, indexes, the organization relationship and the stored form of <see cref="Dataset.FileTypes"/>.</summary>
internal sealed class DatasetConfiguration : IEntityTypeConfiguration<Dataset>
{
    private const char FileTypesSeparator = ';';

    public void Configure(EntityTypeBuilder<Dataset> builder)
    {
        builder.HasIndex(ds => ds.Slug);

        // A slug names one dataset of an organization: the database keeps it so, not only the check before the insert
        // (two requests that pass the check together). The organization is the foreign key's shadow property.
        builder.HasIndex("OrganizationSlug", nameof(Dataset.Slug)).IsUnique();

        // For dataset queries by InternalRef (used in GetUserStorage)
        builder.HasIndex(ds => ds.InternalRef);

        // For dataset queries by creation date (for sorting/filtering)
        builder.HasIndex(ds => ds.CreationDate);

        builder
            .HasOne(ds => ds.Organization)
            .WithMany(org => org.Datasets)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        var valueComparer = new ValueComparer<string[]>(
            (c1, c2) => c1 == c2 || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
            c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c.ToArray());

        // EF does not pass a null FileTypes (the column is nullable) to the converter
        var valueConverter = new ValueConverter<string[]?, string>(
            v => string.Join(FileTypesSeparator, v ?? Array.Empty<string>()),
            v => v.Split(FileTypesSeparator, StringSplitOptions.RemoveEmptyEntries));

        builder
            .Property(e => e.FileTypes)
            .HasConversion(valueConverter, valueComparer);
    }
}
