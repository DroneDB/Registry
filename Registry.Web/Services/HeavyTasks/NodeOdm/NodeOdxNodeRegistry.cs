#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.HeavyTasks.NodeOdx;

/// <summary>
/// Resolves configured NodeODX endpoints (config-based registry for the
/// reduced-scope integration - no DB table / admin UI).
/// </summary>
public sealed class NodeOdxNodeRegistry
{
    private readonly List<NodeOdxEndpoint> _nodes;
    private readonly Dictionary<string, NodeOdxEndpoint> _byId;

    public NodeOdxNodeRegistry(IOptions<ProcessingPlatformSettings> settings)
    {
        var configured = settings.Value.NodeOdx ?? [];

        _nodes =
        [
            .. configured
                .Where(c => !string.IsNullOrWhiteSpace(c.Url))
                .Select(c => new NodeOdxEndpoint(
                    string.IsNullOrWhiteSpace(c.Id) ? "default" : c.Id.Trim(),
                    c.Url.Trim(),
                    string.IsNullOrWhiteSpace(c.Token) ? null : c.Token,
                    c.Title))
        ];

        _byId = new Dictionary<string, NodeOdxEndpoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in _nodes)
            _byId.TryAdd(node.Id, node);
    }

    /// <summary>True when at least one NodeODX endpoint is configured.</summary>
    public bool HasNodes => _nodes.Count > 0;

    /// <summary>All configured endpoints.</summary>
    public IReadOnlyList<NodeOdxEndpoint> All => _nodes;

    /// <summary>
    /// Resolves an endpoint by id. When <paramref name="nodeId"/> is null/empty the
    /// first configured node is returned. Returns null when no match exists.
    /// </summary>
    public NodeOdxEndpoint? Resolve(string? nodeId = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return _nodes.Count > 0 ? _nodes[0] : null;

        return _byId.TryGetValue(nodeId.Trim(), out var node) ? node : null;
    }
}
