#nullable enable
namespace Registry.Web.Services.HeavyTasks.NodeOdx;

/// <summary>
/// A resolved NodeODX endpoint (base URL + optional access token). Built from
/// <see cref="Registry.Web.Models.Configuration.NodeOdxNodeConfig"/>.
/// </summary>
public sealed record NodeOdxEndpoint(string Id, string Url, string? Token, string? Title);

/// <summary>
/// NodeODX task lifecycle status codes (NodeODX REST contract).
/// </summary>
public enum NodeOdxTaskStatusCode
{
    Queued = 10,
    Running = 20,
    Failed = 30,
    Completed = 40,
    Canceled = 50
}

/// <summary>Identity / capacity of a NodeODX instance (subset of <c>GET /info</c>).</summary>
public sealed record NodeOdxInfo(
    string? Version,
    int TaskQueueCount,
    int MaxParallelTasks,
    string? Engine,
    string? EngineVersion);

/// <summary>Current state of a NodeODX task (subset of <c>GET /task/{uuid}/info</c>).</summary>
public sealed record NodeOdxTaskInfo(
    string Uuid,
    NodeOdxTaskStatusCode StatusCode,
    string? ErrorMessage,
    double Progress,
    int ImagesCount);

/// <summary>
/// A single processing option from NodeODX <c>GET /options</c>.
/// <c>Domain</c> is a string (unit label) for scalar types or a string array for enums.
/// </summary>
public sealed record NodeOdxOption(
    string Name,
    string Type,
    object? Domain,
    string? Help,
    object? Value);
