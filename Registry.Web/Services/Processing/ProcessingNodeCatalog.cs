#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks.NodeOdx;

namespace Registry.Web.Services.Processing;

/// <summary>
/// The processing nodes (NodeODX) a user can pick for photogrammetry: their list, their options and their status.
/// Never exposes a node's URL or token.
/// </summary>
public sealed class ProcessingNodeCatalog
{
    private readonly NodeOdxNodeRegistry _nodes;
    private readonly INodeOdxClient _client;
    private readonly ILogger<ProcessingNodeCatalog> _logger;

    public ProcessingNodeCatalog(NodeOdxNodeRegistry nodes, INodeOdxClient client,
        ILogger<ProcessingNodeCatalog> logger)
    {
        _nodes = nodes;
        _client = client;
        _logger = logger;
    }

    /// <summary>The configured nodes, by id and title.</summary>
    public IReadOnlyList<ProcessingNodeDto> List() =>
        [.. _nodes.All.Select(n => new ProcessingNodeDto(n.Id, n.Title ?? n.Id))];

    /// <summary>The processing options of the node, read live from it; <see cref="NotFoundException"/> for an unknown node.</summary>
    public async Task<IReadOnlyList<NodeOdxOptionDto>> GetOptionsAsync(string nodeId, CancellationToken ct)
    {
        var options = await _client.GetOptionsAsync(Resolve(nodeId), ct);
        return [.. options.Select(o => new NodeOdxOptionDto(o.Name, o.Type, o.Domain, o.Help, o.Value))];
    }

    /// <summary>
    /// Whether the node is reachable and its queue capacity; an unreachable node is reported, not thrown.
    /// <see cref="NotFoundException"/> for an unknown node.
    /// </summary>
    public async Task<ProcessingNodeStatusDto> GetStatusAsync(string nodeId, CancellationToken ct)
    {
        var node = Resolve(nodeId);
        try
        {
            var info = await _client.GetInfoAsync(node, ct);
            return new ProcessingNodeStatusDto(node.Id, Reachable: true, info.Version, info.Engine, info.EngineVersion,
                info.TaskQueueCount, info.MaxParallelTasks, ErrorMessage: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NodeODX node '{NodeId}' is unreachable", nodeId);
            return new ProcessingNodeStatusDto(node.Id, Reachable: false, null, null, null, 0, 0,
                ErrorMessage: ex.Message);
        }
    }

    private NodeOdxEndpoint Resolve(string nodeId) =>
        (string.IsNullOrWhiteSpace(nodeId) ? null : _nodes.Resolve(nodeId))
        ?? throw new NotFoundException($"Processing node '{nodeId}' not found.");
}
