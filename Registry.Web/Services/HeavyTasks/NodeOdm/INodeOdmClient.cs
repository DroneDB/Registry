#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.HeavyTasks.NodeOdm;

/// <summary>
/// Thin HTTP client over the NodeODM REST API (OpenDroneMap processing node).
/// Stateless; one instance serves every configured node. See spec §5 (Layer 2),
/// reduced here to a direct NodeODM integration (no generic /v1/node protocol).
/// </summary>
public interface INodeOdmClient
{
    /// <summary>Reads node identity and queue capacity (<c>GET /info</c>).</summary>
    Task<NodeOdmInfo> GetInfoAsync(NodeOdmEndpoint node, CancellationToken ct);

    /// <summary>
    /// Submits a new processing task uploading the given local image files
    /// (<c>POST /task/new</c>). <paramref name="optionsJson"/> is the NodeODM
    /// options array (<c>[{"name":...,"value":...}]</c>) or null. Returns the task uuid.
    /// </summary>
    Task<string> CreateTaskAsync(
        NodeOdmEndpoint node,
        string name,
        IReadOnlyList<string> imageFilePaths,
        string? optionsJson,
        CancellationToken ct);

    /// <summary>Reads task status/progress (<c>GET /task/{uuid}/info</c>).</summary>
    Task<NodeOdmTaskInfo> GetTaskInfoAsync(NodeOdmEndpoint node, string uuid, CancellationToken ct);

    /// <summary>
    /// Reads console output lines starting at <paramref name="sinceLine"/>
    /// (<c>GET /task/{uuid}/output?line=N</c>).
    /// </summary>
    Task<IReadOnlyList<string>> GetTaskOutputAsync(NodeOdmEndpoint node, string uuid, int sinceLine, CancellationToken ct);

    /// <summary>Requests cancellation of a task (<c>POST /task/cancel</c>). Idempotent.</summary>
    Task CancelTaskAsync(NodeOdmEndpoint node, string uuid, CancellationToken ct);

    /// <summary>Removes a task and its workspace (<c>POST /task/remove</c>). Idempotent.</summary>
    Task RemoveTaskAsync(NodeOdmEndpoint node, string uuid, CancellationToken ct);

    /// <summary>
    /// Streams a produced asset to <paramref name="destFilePath"/>
    /// (<c>GET /task/{uuid}/download/{asset}</c>).
    /// </summary>
    Task DownloadAssetAsync(NodeOdmEndpoint node, string uuid, string asset, string destFilePath, CancellationToken ct);
}
