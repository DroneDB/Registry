#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.HeavyTasks.NodeOdm;

/// <summary>
/// <see cref="INodeOdmNodeRegistry"/> built from
/// <see cref="ProcessingPlatformSettings.NodeOdm"/>. Immutable for the process lifetime.
/// </summary>
public sealed class NodeOdmNodeRegistry : INodeOdmNodeRegistry
{
    private readonly List<NodeOdmEndpoint> _nodes;
    private readonly Dictionary<string, NodeOdmEndpoint> _byId;

    public NodeOdmNodeRegistry(IOptions<AppSettings> appSettings)
    {
        var configured = appSettings.Value.ProcessingPlatform?.NodeOdm ?? new List<NodeOdmNodeConfig>();

        _nodes = configured
            .Where(c => !string.IsNullOrWhiteSpace(c.Url))
            .Select(c => new NodeOdmEndpoint(
                string.IsNullOrWhiteSpace(c.Id) ? "default" : c.Id.Trim(),
                c.Url.Trim(),
                string.IsNullOrWhiteSpace(c.Token) ? null : c.Token,
                c.Title))
            .ToList();

        _byId = new Dictionary<string, NodeOdmEndpoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in _nodes)
            _byId.TryAdd(node.Id, node);
    }

    public bool HasNodes => _nodes.Count > 0;

    public IReadOnlyList<NodeOdmEndpoint> All => _nodes;

    public NodeOdmEndpoint? Resolve(string? nodeId = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return _nodes.Count > 0 ? _nodes[0] : null;

        return _byId.TryGetValue(nodeId.Trim(), out var node) ? node : null;
    }
}
