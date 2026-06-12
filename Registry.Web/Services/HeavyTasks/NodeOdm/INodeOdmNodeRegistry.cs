#nullable enable
using System.Collections.Generic;

namespace Registry.Web.Services.HeavyTasks.NodeOdm;

/// <summary>
/// Resolves configured NodeODM endpoints (config-based registry for the
/// reduced-scope integration - no DB table / admin UI).
/// </summary>
public interface INodeOdmNodeRegistry
{
    /// <summary>True when at least one NodeODM endpoint is configured.</summary>
    bool HasNodes { get; }

    /// <summary>All configured endpoints.</summary>
    IReadOnlyList<NodeOdmEndpoint> All { get; }

    /// <summary>
    /// Resolves an endpoint by id. When <paramref name="nodeId"/> is null/empty the
    /// first configured node is returned. Returns null when no match exists.
    /// </summary>
    NodeOdmEndpoint? Resolve(string? nodeId = null);
}
