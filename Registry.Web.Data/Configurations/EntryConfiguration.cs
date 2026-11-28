using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Registry.Web.Data.Models;

namespace Registry.Web.Data.Configurations;

/// <summary>The batch relationship of an upload <see cref="Entry"/>.</summary>
internal sealed class EntryConfiguration : IEntityTypeConfiguration<Entry>
{
    public void Configure(EntityTypeBuilder<Entry> builder)
    {
        builder
            .HasOne(e => e.Batch)
            .WithMany(e => e.Entries)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
