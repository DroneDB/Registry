#nullable disable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Processing;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// The processing nodes (NodeODX) a user can run photogrammetry on (out of <see cref="SystemController"/>):
/// their list, options and status. URLs and tokens are never returned.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class ProcessingNodesController : ControllerBaseEx
{
    private readonly ProcessingNodeCatalog _nodes;

    public ProcessingNodesController(ProcessingNodeCatalog nodes) => _nodes = nodes;

    /// <summary>
    /// Gets the list of configured processing nodes (NodeODX) available for photogrammetry tasks.
    /// Only exposes non-sensitive fields (id and title). URL and token are never returned.
    /// </summary>
    /// <returns>A list of processing node descriptors.</returns>
    [HttpGet("processingNodes", Name = SystemRoutes.Prefix + nameof(GetProcessingNodes))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public ActionResult<IEnumerable<ProcessingNodeDto>> GetProcessingNodes() => Answer<IEnumerable<ProcessingNodeDto>>(_nodes.List());

    /// <summary>
    /// Gets the available processing options from a specific NodeODX processing node.
    /// The options are fetched live from the node and include name, type, domain, help text,
    /// and default value for each option.
    /// </summary>
    /// <param name="nodeId">The id of the processing node.</param>
    /// <returns>A list of processing option descriptors.</returns>
    [HttpGet("processingNodes/{nodeId}/options", Name = SystemRoutes.Prefix + nameof(GetProcessingNodeOptions))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<NodeOdxOptionDto>>> GetProcessingNodeOptions([Required] string nodeId, CancellationToken ct) =>
        Answer<IEnumerable<NodeOdxOptionDto>>(await _nodes.GetOptionsAsync(nodeId, ct));

    /// <summary>
    /// Checks whether a specific NodeODX processing node is reachable and reports its
    /// queue capacity (used by the "Check Node" action before launching photogrammetry).
    /// Never throws for an unreachable node: returns <c>Reachable=false</c> with the
    /// failure reason so the UI can display it.
    /// </summary>
    /// <param name="nodeId">The id of the processing node.</param>
    /// <returns>The node status descriptor.</returns>
    [HttpGet("processingNodes/{nodeId}/status", Name = SystemRoutes.Prefix + nameof(GetProcessingNodeStatus))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProcessingNodeStatusDto>> GetProcessingNodeStatus([Required] string nodeId, CancellationToken ct) =>
        await _nodes.GetStatusAsync(nodeId, ct);
}
