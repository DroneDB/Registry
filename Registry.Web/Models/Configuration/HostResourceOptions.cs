#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>
/// What this process may use of the machine (keys of the <c>AppSettings</c> section): the host budget
/// shared by the Registry processes (XI-06), the background job budget and the native render lanes.
/// <c>Utilities.HostResourceBudget</c> turns them into the effective numbers.
/// </summary>
public sealed class HostResourceOptions : IHasConfigurationDefaults
{
    /// <summary>
    /// Number of worker threads (0 or less: the ASP.NET default). When set, it is also the default
    /// number of light background workers (<see cref="ProcessingBudgetSettings.LightWorkers"/>).
    /// </summary>
    public int WorkerThreads { get; set; }

    /// <summary>Concurrency and native resource budget of background jobs (REG-09).</summary>
    public ProcessingBudgetSettings ProcessingBudget { get; set; } = new();

    /// <summary>Concurrency of native renders (tiles, thumbnails, coverages) in this process.</summary>
    public NativeConcurrencySettings NativeConcurrency { get; set; } = new();

    /// <summary>The machine and the Registry processes that share it (XI-06).</summary>
    public HostBudgetSettings HostBudget { get; set; } = new();

    /// <summary>A section written as <c>null</c> keeps its defaults.</summary>
    public void ApplyDefaults()
    {
        ProcessingBudget ??= new ProcessingBudgetSettings();
        NativeConcurrency ??= new NativeConcurrencySettings();
        HostBudget ??= new HostBudgetSettings();
    }
}
