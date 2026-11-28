using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Registry.Web.Data.Models;

namespace Registry.Web.Data.Configurations;

/// <summary>Column facets and keys of the build notification outbox (<see cref="BuildNotification"/>).</summary>
internal sealed class BuildNotificationConfiguration : IEntityTypeConfiguration<BuildNotification>
{
    public void Configure(EntityTypeBuilder<BuildNotification> e)
    {
        e.HasKey(x => x.Id);

        // The facets of the same columns of the job index (JobIndexConfiguration)
        e.Property(x => x.UserId).HasMaxLength(128).IsRequired();
        e.Property(x => x.OrgSlug).HasMaxLength(128).IsRequired();
        e.Property(x => x.DsSlug).HasMaxLength(128).IsRequired();
        e.Property(x => x.Path).HasMaxLength(2048).IsRequired();
        e.Property(x => x.Hash).HasMaxLength(1024).IsRequired();
        e.Property(x => x.RequestJobId).HasMaxLength(64);

        e.Property(x => x.Outcome).HasMaxLength(16).IsRequired();
        e.Property(x => x.Error).HasMaxLength(BuildNotification.ErrorMaxLength);
        e.Property(x => x.PendingKey).HasMaxLength(64);
        e.Property(x => x.ClaimId).HasMaxLength(32);

        // The unsent lines of a user (the digest, the "first line" check)
        e.HasIndex(x => new { x.UserId, x.SentAtUtc });

        // The same news waits at most once (cleared on send)
        e.HasIndex(x => x.PendingKey).IsUnique();

        // One line per request and outcome, sent or not
        e.HasIndex(x => new { x.RequestJobId, x.Outcome }).IsUnique();

        // The cleanup of old sent lines and of lines whose digest was lost
        e.HasIndex(x => x.FinishedAtUtc);
    }
}
