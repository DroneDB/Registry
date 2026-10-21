using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for managing organizations.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix)]
[Produces("application/json")]
public class OrganizationsController : ControllerBaseEx
{
    private readonly IOrganizationsManager _organizationsManager;

    public OrganizationsController(IOrganizationsManager organizationsManager)
    {
        _organizationsManager = organizationsManager;
    }

    /// <summary>
    /// Gets all organizations.
    /// </summary>
    /// <returns>A list of organizations.</returns>
    [HttpGet(Name = nameof(GetAll))]
    [ProducesResponseType(typeof(IEnumerable<OrganizationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAll([FromQuery] bool ownedOnly = false)
    {
        return Ok(await _organizationsManager.List(ownedOnly));
    }

    /// <summary>
    /// Gets all public organizations for data discovery.
    /// </summary>
    /// <returns>A list of public organizations.</returns>
    [HttpGet("public", Name = nameof(GetAllPublic))]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<OrganizationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAllPublic()
    {
        return Ok(await _organizationsManager.ListPublic());
    }

    /// <summary>
    /// Gets a specific organization by slug.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <returns>The organization information.</returns>
    [HttpGet(RoutesHelper.OrganizationSlug, Name = nameof(Get))]
    [ProducesResponseType(typeof(OrganizationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromRoute, Required] string orgSlug)
    {
        return Ok(await _organizationsManager.Get(orgSlug));

    }

    /// <summary>
    /// Creates a new organization.
    /// </summary>
    /// <param name="organization">The organization data.</param>
    /// <returns>The newly created organization.</returns>
    [HttpPost(Name = nameof(Create))]
    [ProducesResponseType(typeof(OrganizationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromForm, Required] OrganizationDto organization)
    {

        var newOrg = await _organizationsManager.AddNew(organization);
        return CreatedAtRoute(nameof(Get), new { orgSlug = newOrg.Slug },
            newOrg);

    }

    /// <summary>
    /// Updates an existing organization.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="organization">The organization update data.</param>
    /// <returns>No content on success.</returns>
    [HttpPut(RoutesHelper.OrganizationSlug, Name = nameof(Update))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        [FromRoute, Required] string orgSlug,
        [FromForm, Required] OrganizationDto organization)
    {

        await _organizationsManager.Edit(orgSlug, organization);
        return NoContent();

    }

    /// <summary>
    /// Deletes an organization.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete(RoutesHelper.OrganizationSlug, Name = nameof(Delete))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete([FromRoute, Required] string orgSlug)
    {

        await _organizationsManager.Delete(orgSlug);
        return NoContent();

    }

    #region Member Management

    /// <summary>
    /// Gets all members of an organization.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <returns>List of organization members with their permissions.</returns>
    [HttpGet("{orgSlug}/members", Name = nameof(GetMembers))]
    [ProducesResponseType(typeof(IEnumerable<OrganizationMemberDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMembers([FromRoute, Required] string orgSlug)
    {
        return Ok(await _organizationsManager.GetMembers(orgSlug));
    }

    /// <summary>
    /// Adds a new member to an organization.
    /// Requires organization owner, admin permission, or system admin.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="request">The member to add with permission level.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("{orgSlug}/members", Name = nameof(AddMember))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddMember(
        [FromRoute, Required] string orgSlug,
        [FromBody, Required] AddOrganizationMemberDto request)
    {
        await _organizationsManager.AddMember(orgSlug, request.UserName, request.Permissions);
        return NoContent();
    }

    /// <summary>
    /// Updates a member's permission level.
    /// Requires organization owner, admin permission, or system admin.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="userName">The username to update.</param>
    /// <param name="request">The new permission level.</param>
    /// <returns>No content on success.</returns>
    [HttpPut("{orgSlug}/members/{userName}", Name = nameof(UpdateMemberPermission))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMemberPermission(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string userName,
        [FromBody, Required] UpdateMemberPermissionsDto request)
    {
        await _organizationsManager.UpdateMemberPermission(orgSlug, userName, request.Permissions);
        return NoContent();
    }

    /// <summary>
    /// Removes a member from an organization.
    /// Requires organization owner, admin permission, or system admin.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="userName">The username to remove.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("{orgSlug}/members/{userName}", Name = nameof(RemoveMember))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMember(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string userName)
    {
        await _organizationsManager.RemoveMember(orgSlug, userName);
        return NoContent();
    }

    #endregion

}