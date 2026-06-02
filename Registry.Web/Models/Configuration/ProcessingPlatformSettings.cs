#nullable enable
using System.Collections.Generic;

namespace Registry.Web.Models.Configuration;

/// <summary>
/// Processing Platform (Layer 1 - Task substrate) configuration.
/// Bound from the <c>AppSettings:ProcessingPlatform</c> section. See spec §4.9.
/// </summary>
public class ProcessingPlatformSettings
{
    /// <summary>Hours before a produced artifact's WorkDir is swept. Default 24.</summary>
    public int ArtifactTtlHours { get; set; } = 24;

    public int MaxConcurrentTasksPerUser { get; set; } = 3;
    public int MaxQueuedTasksPerUser { get; set; } = 20;
    public int MaxConcurrentTasksPerOrg { get; set; } = 10;
    public int MaxConcurrentTasksGlobal { get; set; } = 32;

    /// <summary>Hard cap on estimated output size per submit (bytes). Default 20 GiB.</summary>
    public long MaxEstimatedOutputBytesPerSubmit { get; set; } = 21474836480;

    /// <summary>Per-org daily output budget in bytes, keyed by org slug ("default" fallback).</summary>
    public Dictionary<string, long> OrgDailyOutputBytes { get; set; } = new()
    {
        ["default"] = 107374182400
    };

    /// <summary>Default tile size (pixels) for windowed raster export. Default 512.</summary>
    public int DefaultRasterTileSize { get; set; } = 512;

    public bool DedupEnabled { get; set; } = true;
    public int DedupLookbackHours { get; set; } = 24;

    public int LogTailMaxLines { get; set; } = 200;
    public int LogTailMaxBytes { get; set; } = 32768;
    public int ProgressUpdateThrottleSeconds { get; set; } = 2;

    public int RemoteNodePollIntervalSeconds { get; set; } = 2;
    public int RemoteNodePollMaxBackoffSeconds { get; set; } = 30;
    public int RemoteNodeRequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// NodeODM processing nodes available for the <c>photogrammetry</c> tool.
    /// Config-based registry (no DB table) for the reduced-scope integration.
    /// </summary>
    public List<NodeOdmNodeConfig> NodeOdm { get; set; } = new();
}

/// <summary>
/// A single NodeODM endpoint (OpenDroneMap processing node) usable by the
/// <c>photogrammetry</c> heavy tool.
/// </summary>
public class NodeOdmNodeConfig
{
    /// <summary>Stable identifier used to target this node from a submit request.</summary>
    public string Id { get; set; } = "default";

    /// <summary>Base URL of the NodeODM instance, e.g. <c>http://localhost:3000</c>.</summary>
    public string Url { get; set; } = "";

    /// <summary>Optional NodeODM access token (passed as the <c>token</c> query param).</summary>
    public string? Token { get; set; }

    /// <summary>Optional human-readable title.</summary>
    public string? Title { get; set; }
}

