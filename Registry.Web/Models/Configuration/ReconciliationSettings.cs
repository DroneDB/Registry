#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>
/// Tuning for the recurring index reconciliation sweep (<c>IndexReconciliationService</c>).
/// Bound from the <c>AppSettings:Reconciliation</c> section. See ImproveParallelWrites plan,
/// workstream 04 §5.2.
/// </summary>
public class ReconciliationSettings
{
    /// <summary>Maximum number of unindexed files re-enqueued per dataset per run.</summary>
    public int MaxItemsPerRun { get; set; } = 500;

    /// <summary>Days a quarantined file is kept before being permanently removed.</summary>
    public int QuarantineRetentionDays { get; set; } = 7;
}
