using System.Collections.Generic;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Utilities;
using static Registry.Web.Services.Configuration.ConfigurationFields;

namespace Registry.Web.Services.Configuration;

/// <summary>The admin configuration sections of the processing platform, the tool gating, the processing and host budgets.</summary>
public sealed class ProcessingSections : IConfigurationSectionBuilder
{
    private readonly ConfigurationView _view;
    private readonly HostResourceBudget _budget;

    public ProcessingSections(ConfigurationView view, HostResourceBudget budget)
    {
        _view = view;
        _budget = budget;
    }

    public IEnumerable<PlacedSection> Build() =>
    [
        new(ConfigurationSection.ProcessingPlatform, BuildProcessingPlatformSection()),
        new(ConfigurationSection.ToolGating, BuildToolGatingSection()),
        new(ConfigurationSection.ProcessingBudget, BuildProcessingBudgetSection()),
        new(ConfigurationSection.HostBudget, BuildHostBudgetSection())
    ];

    private ConfigurationSectionDataDto BuildProcessingPlatformSection()
    {
        var platform = _view.ConfiguredOrNull<ProcessingPlatformSettings>();
        var platformDefault = _view.ShippedOrNull<ProcessingPlatformSettings>();
        var pp = platform;
        var dp = platformDefault;
        var isActive = pp != null;

        var section = Section("ProcessingPlatform", "Processing Platform",
            "Task substrate settings: concurrency limits, dedup, NodeODX nodes, and bulk download thresholds.",
            [
                NumberField("ArtifactTtlHours", "Artifact TTL (hours)",
                    "Hours before a produced artifact's WorkDir is swept.",
                    pp?.ArtifactTtlHours ?? dp?.ArtifactTtlHours ?? 0,
                    dp?.ArtifactTtlHours ?? 0, unit: "hours"),

                NumberField("MaxConcurrentTasksPerUser", "Max Concurrent Tasks / User",
                    "Maximum concurrent heavy tasks per user.",
                    pp?.MaxConcurrentTasksPerUser ?? dp?.MaxConcurrentTasksPerUser ?? 0,
                    dp?.MaxConcurrentTasksPerUser ?? 0),

                NumberField("MaxQueuedTasksPerUser", "Max Queued Tasks / User",
                    "Maximum queued (pending) heavy tasks per user.",
                    pp?.MaxQueuedTasksPerUser ?? dp?.MaxQueuedTasksPerUser ?? 0,
                    dp?.MaxQueuedTasksPerUser ?? 0),

                NumberField("MaxConcurrentTasksPerOrg", "Max Concurrent Tasks / Org",
                    "Maximum concurrent heavy tasks per organization.",
                    pp?.MaxConcurrentTasksPerOrg ?? dp?.MaxConcurrentTasksPerOrg ?? 0,
                    dp?.MaxConcurrentTasksPerOrg ?? 0),

                NumberField("MaxConcurrentTasksGlobal", "Max Concurrent Tasks (global)",
                    "Hard cap on concurrent heavy tasks across all users.",
                    pp?.MaxConcurrentTasksGlobal ?? dp?.MaxConcurrentTasksGlobal ?? 0,
                    dp?.MaxConcurrentTasksGlobal ?? 0),

                NumberField("MaxEstimatedOutputBytesPerSubmit", "Max Estimated Output / Submit",
                    "Hard cap on estimated output size per task submission.",
                    pp?.MaxEstimatedOutputBytesPerSubmit ?? dp?.MaxEstimatedOutputBytesPerSubmit ?? 0,
                    dp?.MaxEstimatedOutputBytesPerSubmit ?? 0, unit: "bytes"),

                NumberField("MaxArchiveExtractSizeBytes", "Max Archive Extract Size",
                    "Maximum compressed archive size accepted by archive-extract tool.",
                    pp?.MaxArchiveExtractSizeBytes ?? dp?.MaxArchiveExtractSizeBytes ?? 0,
                    dp?.MaxArchiveExtractSizeBytes ?? 0, unit: "bytes"),

                NumberField("DiskSafetyMarginBytes", "Disk Safety Margin",
                    "Disk head-room kept free during archive extraction. 0 to disable.",
                    pp?.DiskSafetyMarginBytes ?? dp?.DiskSafetyMarginBytes ?? 0,
                    dp?.DiskSafetyMarginBytes ?? 0, unit: "bytes"),

                NumberField("DefaultRasterTileSize", "Default Raster Tile Size",
                    "Default tile size in pixels for windowed raster export.",
                    pp?.DefaultRasterTileSize ?? dp?.DefaultRasterTileSize ?? 0,
                    dp?.DefaultRasterTileSize ?? 0, unit: "pixels"),

                NumberField("BulkDownloadAsyncThresholdBytes", "Bulk Download Async Threshold",
                    "Size threshold above which bulk downloads are offloaded to async task.",
                    pp?.BulkDownloadAsyncThresholdBytes ?? dp?.BulkDownloadAsyncThresholdBytes ?? 0,
                    dp?.BulkDownloadAsyncThresholdBytes ?? 0, unit: "bytes"),

                NumberField("MaxConcurrentBulkDownloadsPerUser", "Max Concurrent Bulk Downloads / User",
                    "Maximum active bulk-download tasks per user.",
                    pp?.MaxConcurrentBulkDownloadsPerUser ?? dp?.MaxConcurrentBulkDownloadsPerUser ?? 0,
                    dp?.MaxConcurrentBulkDownloadsPerUser ?? 0),

                BoolField("DedupEnabled", "Dedup Enabled",
                    "Enable task deduplication (prevent duplicate submissions).",
                    pp?.DedupEnabled ?? dp?.DedupEnabled ?? false,
                    dp?.DedupEnabled ?? false),

                NumberField("DedupLookbackHours", "Dedup Lookback (hours)",
                    "Time window for deduplication checks.",
                    pp?.DedupLookbackHours ?? dp?.DedupLookbackHours ?? 0,
                    dp?.DedupLookbackHours ?? 0, unit: "hours"),

                NumberField("LogTailMaxLines", "Log Tail Max Lines",
                    "Maximum lines returned by log tail endpoint.",
                    pp?.LogTailMaxLines ?? dp?.LogTailMaxLines ?? 0,
                    dp?.LogTailMaxLines ?? 0),

                NumberField("LogTailMaxBytes", "Log Tail Max Bytes",
                    "Maximum bytes returned by log tail endpoint.",
                    pp?.LogTailMaxBytes ?? dp?.LogTailMaxBytes ?? 0,
                    dp?.LogTailMaxBytes ?? 0, unit: "bytes"),

                NumberField("ProgressUpdateThrottleSeconds", "Progress Update Throttle",
                    "Minimum seconds between progress updates.",
                    pp?.ProgressUpdateThrottleSeconds ?? dp?.ProgressUpdateThrottleSeconds ?? 0,
                    dp?.ProgressUpdateThrottleSeconds ?? 0, unit: "seconds"),

                NumberField("RemoteNodePollIntervalSeconds", "Remote Node Poll Interval",
                    "Poll interval for remote NodeODX nodes.",
                    pp?.RemoteNodePollIntervalSeconds ?? dp?.RemoteNodePollIntervalSeconds ?? 0,
                    dp?.RemoteNodePollIntervalSeconds ?? 0, unit: "seconds"),

                NumberField("RemoteNodePollMaxBackoffSeconds", "Remote Node Max Backoff",
                    "Maximum backoff for remote node polling.",
                    pp?.RemoteNodePollMaxBackoffSeconds ?? dp?.RemoteNodePollMaxBackoffSeconds ?? 0,
                    dp?.RemoteNodePollMaxBackoffSeconds ?? 0, unit: "seconds"),

                NumberField("RemoteNodeRequestTimeoutSeconds", "Remote Node Request Timeout",
                    "HTTP request timeout for remote NodeODX nodes.",
                    pp?.RemoteNodeRequestTimeoutSeconds ?? dp?.RemoteNodeRequestTimeoutSeconds ?? 0,
                    dp?.RemoteNodeRequestTimeoutSeconds ?? 0, unit: "seconds"),

                NumberField("MaxConcurrentUrlImportsPerUser", "Max Concurrent URL Imports / User",
                    "Maximum active import-file (single-file URL import) tasks per user.",
                    pp?.MaxConcurrentUrlImportsPerUser ?? dp?.MaxConcurrentUrlImportsPerUser ?? 0,
                    dp?.MaxConcurrentUrlImportsPerUser ?? 0),

                JsonField("OrgDailyOutputBytes", "Per-Org Daily Output Budget (bytes)",
                    "Per-organization daily output size budget in bytes, keyed by org slug ('default' is the fallback).",
                    pp?.OrgDailyOutputBytes, dp?.OrgDailyOutputBytes),

                JsonField("NodeOdx", "NodeODX Processing Nodes",
                    "Array of NodeODX processing node configurations. Each entry: { Id, Url, Token, Title }.",
                    pp?.NodeOdx, dp?.NodeOdx)
            ]);

        section.IsActive = isActive;
        return section;
    }

    private ConfigurationSectionDataDto BuildToolGatingSection()
    {
        var platform = _view.ConfiguredOrNull<ProcessingPlatformSettings>();
        var pp = platform;
        var isActive = pp?.Tools is { Count: > 0 };

        // Section name is "ProcessingPlatform" so the client merges the generated JSON
        // under the same AppSettings:ProcessingPlatform node as the main section.
        var section = Section("ProcessingPlatform", "Tool Gating",
            "Per-tool feature gating: hide or disable specific heavy tools, and restrict them by role or organization.",
            [
                JsonField("Tools", "Tool Gating Config",
                    "JSON object keyed by tool id (e.g. \"photogrammetry\"). Each entry accepts: " +
                    "availability (Enabled|Disabled|Hidden), disabledMessage, allowedRoles (array), " +
                    "allowedOrgs (array), hideWhenNotAllowed (bool), maxConcurrentPerUser, maxQueuedPerUser. " +
                    "Omit a tool to use the default (Enabled, no restrictions).",
                    isActive ? pp!.Tools : null,
                    null)
            ]);

        section.IsActive = isActive;
        return section;
    }

    private ConfigurationSectionDataDto BuildProcessingBudgetSection()
    {
        var host = _view.Configured<HostResourceOptions>().WithDefaults();
        var hostDefault = _view.Shipped<HostResourceOptions>().WithDefaults();
        var pb = host.ProcessingBudget;
        var db = hostDefault.ProcessingBudget;
        // A job host shows its own budget; the web server shows a processing node's on this machine
        var effective = (_budget.Processing ?? _budget.For(InstanceType.ProcessingNode).Processing)!;

        return Section("ProcessingBudget", "Processing Budget",
            $"Background job concurrency and per-build native limits. 0 = derived from the host. Effective: {effective}.",
            [
                NumberField("HeavyWorkers", "Heavy Workers",
                    $"Concurrent jobs that run native builds (\"builds\" queue). Effective: {effective.HeavyWorkers}.",
                    pb.HeavyWorkers, db.HeavyWorkers, minValue: 0),

                NumberField("LightWorkers", "Light Workers",
                    $"Concurrent other jobs (\"tasks\" and \"default\" queues). Effective: {effective.LightWorkers}.",
                    pb.LightWorkers, db.LightWorkers, minValue: 0),

                NumberField("BuildThreads", "Build Threads",
                    $"Threads per native build (DDB_BUILD_THREADS). Effective: {effective.BuildThreads}.",
                    pb.BuildThreads, db.BuildThreads, minValue: 0),

                NumberField("BuildCacheMb", "Build Cache",
                    $"GDAL block cache during a raster build (DDB_COG_CACHE_MB). Effective: {effective.BuildCacheMb} MB.",
                    pb.BuildCacheMb, db.BuildCacheMb, minValue: 0, unit: "MB"),

                NumberField("BuildWarpMemoryMb", "Build Warp Memory",
                    $"GDAL warp memory during a raster build (DDB_WARP_MEMORY_MB). Effective: {effective.BuildWarpMemoryMb} MB.",
                    pb.BuildWarpMemoryMb, db.BuildWarpMemoryMb, minValue: 0, unit: "MB")
            ]);
    }

    private ConfigurationSectionDataDto BuildHostBudgetSection()
    {
        var host = _view.Configured<HostResourceOptions>().WithDefaults();
        var hostDefault = _view.Shipped<HostResourceOptions>().WithDefaults();
        var hb = host.HostBudget;
        var db = hostDefault.HostBudget;

        return Section("HostBudget", "Host Budget",
            "The machine and the Registry processes that share it: each process takes its weight's share of " +
            "the cores and memory (processing budget, render lanes, libddb's CPU pool). " +
            (_budget.Declared ? $"This process: {_budget}." : "Not declared: each process plans the whole machine."),
            [
                NumberField("Cores", "Cores", "Logical processors of the machine; 0 = what each process sees.",
                    hb.Cores, db.Cores, minValue: 0),
                NumberField("MemoryMb", "Memory", "Memory of the machine; 0 = what each process sees.",
                    hb.MemoryMb, db.MemoryMb, minValue: 0, unit: "MB"),
                NumberField("WebServers", "Web Servers", "Web server processes on the machine.",
                    hb.WebServers, db.WebServers, minValue: 0),
                NumberField("ProcessingNodes", "Processing Nodes", "Processing node processes on the machine.",
                    hb.ProcessingNodes, db.ProcessingNodes, minValue: 0),
                NumberField("ThumbnailGenerators", "Thumbnail Generators",
                    "Thumbnail generator processes on the machine.",
                    hb.ThumbnailGenerators, db.ThumbnailGenerators, minValue: 0),
                NumberField("IoThreads", "I/O Threads",
                    "Disk-bound threads for the whole machine, split like the cores; 0 = libddb's default per process.",
                    hb.IoThreads, db.IoThreads, minValue: 0)
            ]);
    }
}
