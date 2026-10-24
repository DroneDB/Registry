using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Registry.Web.Data.Models;

namespace Registry.Web.Data.Configurations;

/// <summary>Column facets and the lookup indexes of the task index (<see cref="JobIndex"/>).</summary>
internal sealed class JobIndexConfiguration : IEntityTypeConfiguration<JobIndex>
{
    public void Configure(EntityTypeBuilder<JobIndex> e)
    {
        e.HasKey(x => x.JobId);
        e.Property(x => x.JobId).HasMaxLength(64);
        e.Property(x => x.OrgSlug).HasMaxLength(128).IsRequired();
        e.Property(x => x.DsSlug).HasMaxLength(128).IsRequired();
        e.Property(x => x.Hash).HasMaxLength(1024);
        e.Property(x => x.Path).HasMaxLength(2048);
        e.Property(x => x.UserId).HasMaxLength(128);
        e.Property(x => x.Queue).HasMaxLength(64);
        e.Property(x => x.CurrentState).HasMaxLength(32).IsRequired();
        e.Property(x => x.MethodDisplay).HasMaxLength(1024);

        // Processing Platform (Layer 1) columns
        e.Property(x => x.ToolId).HasMaxLength(64).IsRequired().HasDefaultValue("build");
        e.Property(x => x.ToolVersion).HasMaxLength(16).IsRequired().HasDefaultValue("1");
        e.Property(x => x.PhaseMessage).HasMaxLength(256);
        e.Property(x => x.ArtifactSha256).HasMaxLength(64);
        e.Property(x => x.ErrorType).HasMaxLength(128);
        e.Property(x => x.RequestHash).HasMaxLength(64);
        e.Property(x => x.ParentJobId).HasMaxLength(36);
        e.Property(x => x.WorkflowExecutionId).HasMaxLength(36);

        e.HasIndex(x => new { x.OrgSlug, x.DsSlug });
        e.HasIndex(x => new { x.OrgSlug, x.DsSlug, x.Hash });
        e.HasIndex(x => x.UserId);
        e.HasIndex(x => x.CreatedAtUtc);

        // Processing Platform (Layer 1) indexes
        e.HasIndex(x => new { x.Queue, x.OrgSlug, x.DsSlug, x.ToolId, x.CurrentState, x.CreatedAtUtc })
            .HasDatabaseName("IX_JobIndex_Tool_State");
        e.HasIndex(x => new { x.OrgSlug, x.DsSlug, x.ToolId, x.RequestHash })
            .HasDatabaseName("IX_JobIndex_RequestHash");
        e.HasIndex(x => x.WorkflowExecutionId)
            .HasDatabaseName("IX_JobIndex_Workflow");
        e.HasIndex(x => x.ParentJobId)
            .HasDatabaseName("IX_JobIndex_Parent");
    }
}
