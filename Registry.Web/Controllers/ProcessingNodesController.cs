using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Features;
using Registry.Web.Services.Maintenance;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Processing;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Controllers;

/// <summary>
/// The processing nodes (NodeODX) a user can run photogrammetry on (RQ-12, out of <see cref="SystemController"/>):
/// their list, options and status. URLs and tokens are never returned.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.SystemRadix)]
[Produces("application/json")]
public class ProcessingNodesController : ControllerBaseEx
{
    private readonly IProcessingNodeCatalog _nodes;

    public ProcessingNodesController(IProcessingNodeCatalog nodes) => _nodes = nodes;

    /// <summary>
    /// Gets the list of configured processing nodes (NodeODX) available for photogrammetry tasks.
    /// Only exposes non-sensitive fields (id and title). URL and token are never returned.
    /// </summary>
    /// <returns>A list of processing node descriptors.</returns>
    [HttpGet("processingNodes", Name = SystemRoutes.Prefix + nameof(GetProcessingNodes))]
    [ProducesResponseType(typeof(IEnumerable<ProcessingNodeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public IActionResult GetProcessingNodes() => Ok(_nodes.List());

    /// <summary>
    /// Gets the available processing options from a specific NodeODX processing node.
    /// The options are fetched live from the node and include name, type, domain, help text,
    /// and default value for each option.
    /// </summary>
    /// <param name="nodeId">The id of the processing node.</param>
    /// <returns>A list of processing option descriptors.</returns>
    [HttpGet("processingNodes/{nodeId}/options", Name = SystemRoutes.Prefix + nameof(GetProcessingNodeOptions))]
    [ProducesResponseType(typeof(IEnumerable<NodeOdxOptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProcessingNodeOptions([Required] string nodeId) =>
        Ok(await _nodes.GetOptionsAsync(nodeId, HttpContext.RequestAborted));

    /// <summary>
    /// Checks whether a specific NodeODX processing node is reachable and reports its
    /// queue capacity (used by the "Check Node" action before launching photogrammetry).
    /// Never throws for an unreachable node: returns <c>Reachable=false</c> with the
    /// failure reason so the UI can display it.
    /// </summary>
    /// <param name="nodeId">The id of the processing node.</param>
    /// <returns>The node status descriptor.</returns>
    [HttpGet("processingNodes/{nodeId}/status", Name = SystemRoutes.Prefix + nameof(GetProcessingNodeStatus))]
    [ProducesResponseType(typeof(ProcessingNodeStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProcessingNodeStatus([Required] string nodeId) =>
        Ok(await _nodes.GetStatusAsync(nodeId, HttpContext.RequestAborted));
}
