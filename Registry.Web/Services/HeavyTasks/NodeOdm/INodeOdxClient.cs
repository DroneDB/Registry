#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.HeavyTasks.NodeOdx;

/// <summary>
/// Thin HTTP client over the NodeODX REST API (OpenDroneMap processing node).
/// Stateless; one instance serves every configured node. Narrowed to a direct
/// NodeODX integration (no generic /v1/node protocol).
/// </summary>
public interface INodeOdxClient
{
    /// <summary>Reads node identity and queue capacity (<c>GET /info</c>).</summary>
    Task<NodeOdxInfo> GetInfoAsync(NodeOdxEndpoint node, CancellationToken ct);

    /// <summary>
    /// Submits a new processing task uploading the given local image files
    /// (<c>POST /task/new</c>). <paramref name="optionsJson"/> is the NodeODX
    /// options array (<c>[{"name":...,"value":...}]</c>) or null. Returns the task uuid.
    /// </summary>
    Task<string> CreateTaskAsync(
        NodeOdxEndpoint node,
        string name,
        IReadOnlyList<string> imageFilePaths,
        string? optionsJson,
        CancellationToken ct);

    /// <summary>Reads task status/progress (<c>GET /task/{uuid}/info</c>).</summary>
    Task<NodeOdxTaskInfo> GetTaskInfoAsync(NodeOdxEndpoint node, string uuid, CancellationToken ct);

    /// <summary>
    /// Reads console output lines starting at <paramref name="sinceLine"/>
    /// (<c>GET /task/{uuid}/output?line=N</c>).
    /// </summary>
    Task<IReadOnlyList<string>> GetTaskOutputAsync(NodeOdxEndpoint node, string uuid, int sinceLine, CancellationToken ct);

    /// <summary>Requests cancellation of a task (<c>POST /task/cancel</c>). Idempotent.</summary>
    Task CancelTaskAsync(NodeOdxEndpoint node, string uuid, CancellationToken ct);

    /// <summary>Removes a task and its workspace (<c>POST /task/remove</c>). Idempotent.</summary>
    Task RemoveTaskAsync(NodeOdxEndpoint node, string uuid, CancellationToken ct);

    /// <summary>
    /// Streams a produced asset to <paramref name="destFilePath"/>
    /// (<c>GET /task/{uuid}/download/{asset}</c>).
    /// </summary>
    Task DownloadAssetAsync(NodeOdxEndpoint node, string uuid, string asset, string destFilePath, CancellationToken ct);

    /// <summary>
    /// Retrieves the list of available processing options from the node
    /// (<c>GET /options</c>). Each option includes name, type, domain, help text,
    /// and default value.
    /// </summary>
    Task<IReadOnlyList<NodeOdxOption>> GetOptionsAsync(NodeOdxEndpoint node, CancellationToken ct);
}
