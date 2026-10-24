#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Utilities;

/// <summary>
/// What this process may use of the machine (XI-06): one budget per host, computed once from the
/// instance type, the machine and <see cref="HostResourceOptions"/>, and consumed by the Hangfire servers
/// (<see cref="Processing"/>), the native render gate (<see cref="RenderTiles"/>, <see cref="RenderHeavy"/>)
/// and libddb (<see cref="Ddb"/>, passed through <c>DDBConfigure</c>; <see cref="NativeBuildEnvironment"/>).
/// <para>
/// The share of a process is its weight over the sum of the weights of the processes declared in
/// <see cref="HostBudgetSettings"/> (the process counts once for its own role when the role is not
/// declared; the all-in-one instance has the web server and processing node weights). With nothing
/// declared the share is 1: each process plans the whole machine, as before the budget existed.
/// </para>
/// </summary>
public sealed class HostResourceBudget
{
    private readonly HostResourceOptions _options;
    private readonly DdbRuntimeOptions _ddb;
    private readonly Func<string, string?> _environment;

    private HostResourceBudget(InstanceType role, double share, HostFacts machine, HostFacts process,
        ProcessingBudget? processing, int renderTiles, int renderHeavy, DdbProcessSettings ddb, bool declared,
        HostResourceOptions options, DdbRuntimeOptions ddbOptions, Func<string, string?> environment)
    {
        Role = role;
        Share = share;
        Machine = machine;
        Process = process;
        Processing = processing;
        RenderTiles = renderTiles;
        RenderHeavy = renderHeavy;
        Ddb = ddb;
        Declared = declared;
        _options = options;
        _ddb = ddbOptions;
        _environment = environment;
    }

    /// <summary>The instance type of the process.</summary>
    public InstanceType Role { get; }

    /// <summary>The share of the machine (0 to 1; 1 when no budget is declared).</summary>
    public double Share { get; }

    /// <summary>The machine: the declared size, or what this process sees.</summary>
    public HostFacts Machine { get; }

    /// <summary>The cores and memory of this process's share.</summary>
    public HostFacts Process { get; }

    /// <summary>The background job budget of a job host (all-in-one instance, processing node); null on the others.</summary>
    public ProcessingBudget? Processing { get; }

    /// <summary>Width of the native tiles lane.</summary>
    public int RenderTiles { get; }

    /// <summary>Width of the native heavy lane (thumbnails, coverages).</summary>
    public int RenderHeavy { get; }

    /// <summary>The <c>DDBConfigure</c> settings: explicit <c>Ddb</c> values, else the derived ones (null: libddb's environment or default).</summary>
    public DdbProcessSettings Ddb { get; }

    /// <summary>True when the deployment declared a host budget.</summary>
    public bool Declared { get; }

    /// <summary>The budget a process of <paramref name="role"/> gets on the same machine with the same options.</summary>
    public HostResourceBudget For(InstanceType role) => Create(role, Machine, _options, _ddb, _environment);

    /// <summary>libddb's environment variable for <see cref="DdbProcessSettings.CpuPoolSize"/>.</summary>
    public const string CpuPoolSizeVariable = "DDB_CPU_POOL_SIZE";

    /// <summary>libddb's environment variable for <see cref="DdbProcessSettings.IoThreads"/>.</summary>
    public const string IoThreadsVariable = "DDB_IO_THREADS";

    private const long BytesPerMiB = 1024L * 1024;

    /// <summary>
    /// The budget of a process of <paramref name="role"/> on a machine where it sees <paramref name="detected"/>.
    /// </summary>
    /// <param name="role">The instance type of this process.</param>
    /// <param name="detected">The cores and memory this process sees.</param>
    /// <param name="options">The host budget, the job budget and the render lanes.</param>
    /// <param name="ddb">libddb's explicit settings (section <c>Ddb</c>): an explicit value always wins.</param>
    /// <param name="environment">Reads an environment variable: a derived libddb value never overrides one.</param>
    public static HostResourceBudget Create(InstanceType role, HostFacts detected, HostResourceOptions options,
        DdbRuntimeOptions ddb, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ddb);
        ArgumentNullException.ThrowIfNull(environment);

        var host = options.HostBudget;
        var machine = new HostFacts(
            host.Cores > 0 ? host.Cores : detected.Cores,
            host.MemoryMb > 0 ? host.MemoryMb * BytesPerMiB : detected.MemoryBytes);

        var share = ShareOf(role, host);
        var process = new HostFacts(
            Math.Max(1, (int)Math.Floor(Math.Max(1, machine.Cores) * share)),
            (long)(machine.MemoryBytes * share));

        var processing = role is InstanceType.Default or InstanceType.ProcessingNode
            ? ProcessingBudget.Derive(process, options.ProcessingBudget, options.WorkerThreads)
            : null;

        var lanes = options.NativeConcurrency;
        var declared = host.IsDeclared;

        var cpuPool = ddb.CpuPoolSize ??
                      (declared && IsUnset(environment, CpuPoolSizeVariable) ? Math.Max(0, process.Cores - 1) : null);
        var ioThreads = ddb.IoThreads ??
                        (host.IoThreads > 0 && IsUnset(environment, IoThreadsVariable)
                            ? Math.Max(1, (int)Math.Round(host.IoThreads * share, MidpointRounding.AwayFromZero))
                            : null);

        return new HostResourceBudget(role, share, machine, process, processing,
            lanes.TilesFor(process.Cores), lanes.HeavyFor(process.Cores),
            new DdbProcessSettings(cpuPool, ioThreads, ddb.MinFreeDiskMb, ddb.CacheDir, ddb.GdalCacheMb), declared,
            options, ddb, environment);
    }

    /// <summary>
    /// The share of a process of <paramref name="role"/>: its weight over the weights of the declared
    /// processes (this one included once when its role is not declared).
    /// </summary>
    public static double ShareOf(InstanceType role, HostBudgetSettings host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var own = role switch
        {
            InstanceType.WebServer => host.WebServerWeight,
            InstanceType.ProcessingNode => host.ProcessingNodeWeight,
            InstanceType.ThumbnailGenerator => host.ThumbnailGeneratorWeight,
            _ => host.WebServerWeight + host.ProcessingNodeWeight
        };

        var total = Count(role, InstanceType.WebServer, host.WebServers) * host.WebServerWeight +
                    Count(role, InstanceType.ProcessingNode, host.ProcessingNodes) * host.ProcessingNodeWeight +
                    Count(role, InstanceType.ThumbnailGenerator, host.ThumbnailGenerators) * host.ThumbnailGeneratorWeight +
                    (role == InstanceType.Default ? own : 0);

        return total <= 0 ? 1 : Math.Clamp(own / total, 0, 1);
    }

    /// <summary>
    /// The per-build limits libddb reads from the environment (DDB-08) on a job host; a value set in the
    /// deployment environment wins over the budget. Empty on a host that runs no builds.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)> NativeBuildEnvironment => Processing is { } p
        ?
        [
            ("DDB_BUILD_THREADS", p.BuildThreads.ToString(CultureInfo.InvariantCulture)),
            ("DDB_COG_CACHE_MB", p.BuildCacheMb.ToString(CultureInfo.InvariantCulture)),
            ("DDB_WARP_MEMORY_MB", p.BuildWarpMemoryMb.ToString(CultureInfo.InvariantCulture))
        ]
        : [];

    public override string ToString() =>
        $"{Role}: {Share:P0} of {Machine.Cores} core(s) and {Machine.MemoryBytes / BytesPerMiB} MiB" +
        $"{(Declared ? "" : " (no host budget declared: the whole machine)")} = {Process.Cores} core(s), " +
        $"{Process.MemoryBytes / BytesPerMiB} MiB; render lanes {RenderTiles} tiles / {RenderHeavy} heavy; " +
        $"libddb cpuPoolSize {Describe(Ddb.CpuPoolSize)}, ioThreads {Describe(Ddb.IoThreads)}, gdalCacheMb {Describe(Ddb.GdalCacheMb)}" +
        (Processing is { } p ? $"; jobs {p}" : "");

    private static int Count(InstanceType role, InstanceType counted, int declared) =>
        declared > 0 ? declared : role == counted ? 1 : 0;

    private static bool IsUnset(Func<string, string?> environment, string name) =>
        string.IsNullOrEmpty(environment(name));

    private static string Describe(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "libddb default";
}
