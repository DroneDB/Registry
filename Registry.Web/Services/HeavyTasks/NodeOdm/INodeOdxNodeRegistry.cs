#nullable enable
using System.Collections.Generic;

namespace Registry.Web.Services.HeavyTasks.NodeOdx;

/// <summary>
/// Resolves configured NodeODX endpoints (config-based registry for the
/// reduced-scope integration - no DB table / admin UI).
/// </summary>
public interface INodeOdxNodeRegistry
{
    /// <summary>True when at least one NodeODX endpoint is configured.</summary>
    bool HasNodes { get; }

    /// <summary>All configured endpoints.</summary>
    IReadOnlyList<NodeOdxEndpoint> All { get; }

    /// <summary>
    /// Resolves an endpoint by id. When <paramref name="nodeId"/> is null/empty the
    /// first configured node is returned. Returns null when no match exists.
    /// </summary>
    NodeOdxEndpoint? Resolve(string? nodeId = null);
}
