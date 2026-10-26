#nullable enable
using System;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Utilities;

/// <summary>Host facts the processing budget is derived from.</summary>
/// <param name="Cores">Logical processors available to the process.</param>
/// <param name="MemoryBytes">Memory available to the process (container limit when there is one).</param>
public sealed record HostFacts(int Cores, long MemoryBytes)
{
    public static HostFacts Current() =>
        new(Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
}

/// <summary>
/// Effective concurrency and per-build resources for background jobs: one source of truth for
/// the Hangfire servers (worker counts), the queue routing and the native build options.
/// <para>
/// A native raster build needs about <see cref="BuildCacheMb"/> + <see cref="BuildWarpMemoryMb"/>
/// + ~1 GB of driver temporaries (2.4–2.8 GB measured for a 72 MB, 40k × 90k px source),
/// hence one heavy worker per 4 GB; heavy workers × build threads stays within the cores.
/// </para>
/// </summary>
public sealed record ProcessingBudget(
    int HeavyWorkers,
    int LightWorkers,
    int BuildThreads,
    int BuildCacheMb,
    int BuildWarpMemoryMb,
    int BackgroundRebuildThreads = ProcessingBudget.DefaultBackgroundRebuildThreads)
{
    public const int DefaultBuildCacheMb = 1024;
    public const int DefaultBuildWarpMemoryMb = 512;

    /// <summary>
    /// Threads of a background rebuild when none is set: the rebuild queue runs one build at a time all day, so it
    /// takes a small part of the host and leaves the rest to the builds users wait for.
    /// </summary>
    public const int DefaultBackgroundRebuildThreads = 2;

    /// <summary>libddb refuses a build thread cap above this (<c>DDBBuildEx</c>).</summary>
    public const int MaxBackgroundRebuildThreads = 4096;

    private const long BytesPerHeavyWorker = 4L * 1024 * 1024 * 1024;

    /// <param name="host">Cores and memory of the host.</param>
    /// <param name="settings">Explicit values; 0 means "derive".</param>
    /// <param name="legacyWorkerThreads">AppSettings.WorkerThreads: default for the light workers when set.</param>
    public static ProcessingBudget Derive(HostFacts host, ProcessingBudgetSettings? settings, int legacyWorkerThreads)
    {
        ArgumentNullException.ThrowIfNull(host);
        settings ??= new ProcessingBudgetSettings();

        var cores = Math.Max(1, host.Cores);
        var byMemory = (int)Math.Max(1, host.MemoryBytes / BytesPerHeavyWorker);

        var heavy = settings.HeavyWorkers > 0
            ? settings.HeavyWorkers
            : Math.Clamp(byMemory, 1, Math.Max(1, cores / 8));

        // Light jobs run no native builds but can hold a worker for hours (remote processing
        // polls on "tasks"): as many as before the budget, so they cannot starve "default"
        var light = settings.LightWorkers > 0 ? settings.LightWorkers
            : legacyWorkerThreads > 0 ? legacyWorkerThreads
            : Math.Max(2, cores);

        var threads = settings.BuildThreads > 0
            ? settings.BuildThreads
            : Math.Clamp(cores / heavy, 2, 8);

        return new ProcessingBudget(heavy, light, threads,
            settings.BuildCacheMb > 0 ? settings.BuildCacheMb : DefaultBuildCacheMb,
            settings.BuildWarpMemoryMb > 0 ? settings.BuildWarpMemoryMb : DefaultBuildWarpMemoryMb,
            settings.BackgroundRebuildThreads > 0
                ? Math.Min(settings.BackgroundRebuildThreads, MaxBackgroundRebuildThreads)
                : DefaultBackgroundRebuildThreads);
    }

    public override string ToString() =>
        $"{HeavyWorkers} heavy worker(s) x {BuildThreads} build thread(s), {LightWorkers} light worker(s), " +
        $"1 background rebuild x {BackgroundRebuildThreads} thread(s), build cache {BuildCacheMb} MB, warp memory {BuildWarpMemoryMb} MB";
}
