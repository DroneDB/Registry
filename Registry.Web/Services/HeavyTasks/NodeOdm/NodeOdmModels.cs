#nullable enable
namespace Registry.Web.Services.HeavyTasks.NodeOdm;

/// <summary>
/// A resolved NodeODM endpoint (base URL + optional access token). Built from
/// <see cref="Registry.Web.Models.Configuration.NodeOdmNodeConfig"/>.
/// </summary>
public sealed record NodeOdmEndpoint(string Id, string Url, string? Token, string? Title);

/// <summary>NodeODM task lifecycle status codes (NodeODM REST contract).</summary>
public enum NodeOdmTaskStatusCode
{
    Queued = 10,
    Running = 20,
    Failed = 30,
    Completed = 40,
    Canceled = 50
}

/// <summary>Identity / capacity of a NodeODM instance (subset of <c>GET /info</c>).</summary>
public sealed record NodeOdmInfo(
    string? Version,
    int TaskQueueCount,
    int MaxParallelTasks,
    string? Engine,
    string? EngineVersion);

/// <summary>Current state of a NodeODM task (subset of <c>GET /task/{uuid}/info</c>).</summary>
public sealed record NodeOdmTaskInfo(
    string Uuid,
    NodeOdmTaskStatusCode StatusCode,
    string? ErrorMessage,
    double Progress,
    int ImagesCount);
