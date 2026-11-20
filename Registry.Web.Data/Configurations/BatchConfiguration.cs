using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Registry.Web.Data.Models;

namespace Registry.Web.Data.Configurations;

/// <summary>Indexes of the upload batches and their dataset relationship.</summary>
internal sealed class BatchConfiguration : IEntityTypeConfiguration<Batch>
{
    public void Configure(EntityTypeBuilder<Batch> builder)
    {
        // For batch queries by status
        builder.HasIndex(b => b.Status);

        // For batch queries by user and status
        builder.HasIndex(b => new { b.UserName, b.Status });

        // For batch queries by start date
        builder.HasIndex(b => b.Start);

        builder
            .HasOne(e => e.Dataset)
            .WithMany(e => e.Batches)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
