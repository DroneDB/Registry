#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.HeavyTasks.NodeOdx;

/// <summary>
/// <see cref="INodeOdxNodeRegistry"/> built from
/// <see cref="ProcessingPlatformSettings.NodeOdx"/>. Immutable for the process lifetime.
/// </summary>
public sealed class NodeOdxNodeRegistry : INodeOdxNodeRegistry
{
    private readonly List<NodeOdxEndpoint> _nodes;
    private readonly Dictionary<string, NodeOdxEndpoint> _byId;

    public NodeOdxNodeRegistry(IOptions<AppSettings> appSettings)
    {
        var configured = appSettings.Value.ProcessingPlatform?.NodeOdx ?? [];

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

    public bool HasNodes => _nodes.Count > 0;

    public IReadOnlyList<NodeOdxEndpoint> All => _nodes;

    public NodeOdxEndpoint? Resolve(string? nodeId = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return _nodes.Count > 0 ? _nodes[0] : null;

        return _byId.TryGetValue(nodeId.Trim(), out var node) ? node : null;
    }
}
