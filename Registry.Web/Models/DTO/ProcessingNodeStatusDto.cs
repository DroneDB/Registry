#nullable enable
namespace Registry.Web.Models.DTO;

/// <summary>
/// Health/status of a NodeODX processing node, returned by
/// <c>GET /sys/processingNodes/{nodeId}/status</c>. When the node cannot be reached,
/// <see cref="Reachable"/> is <c>false</c> and <see cref="ErrorMessage"/> carries the reason.
/// </summary>
/// <param name="Id">The queried node id.</param>
/// <param name="Reachable">True when the node responded to the info request.</param>
/// <param name="Version">NodeODX version string (when reachable).</param>
/// <param name="Engine">Processing engine name (when reported).</param>
/// <param name="EngineVersion">Processing engine version (when reported).</param>
/// <param name="TaskQueueCount">Number of tasks currently queued/running on the node.</param>
/// <param name="MaxParallelTasks">Maximum parallel tasks the node accepts.</param>
/// <param name="ErrorMessage">Failure reason when <see cref="Reachable"/> is <c>false</c>.</param>
public sealed record ProcessingNodeStatusDto(
    string Id,
    bool Reachable,
    string? Version,
    string? Engine,
    string? EngineVersion,
    int TaskQueueCount,
    int MaxParallelTasks,
    string? ErrorMessage);
