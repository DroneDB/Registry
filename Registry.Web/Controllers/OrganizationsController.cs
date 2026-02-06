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
    private readonly ILogger<OrganizationsController> _logger;

    public OrganizationsController(IOrganizationsManager organizationsManager, ILogger<OrganizationsController> logger)
    {
        _organizationsManager = organizationsManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets all organizations.
    /// </summary>
    /// <returns>A list of organizations.</returns>
    [HttpGet(Name = nameof(GetAll))]
    [ProducesResponseType(typeof(IEnumerable<OrganizationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAll()
    {
        try
        {
            _logger.LogDebug("Organizations controller GetAll()");

            return Ok(await _organizationsManager.List());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Organizations controller GetAll()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets all public organizations for data discovery.
    /// </summary>
    /// <returns>A list of public organizations.</returns>
    [HttpGet("public", Name = nameof(GetAllPublic))]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<OrganizationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAllPublic()
    {
        try
        {
            _logger.LogDebug("Organizations controller GetAllPublic()");

            return Ok(await _organizationsManager.ListPublic());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Organizations controller GetAllPublic()");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets a specific organization by slug.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <returns>The organization information.</returns>
    [HttpGet(RoutesHelper.OrganizationSlug, Name = nameof(Get))]
    [ProducesResponseType(typeof(OrganizationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromRoute, Required] string orgSlug)
    {
        try
        {
            _logger.LogDebug("Organizations controller Get('{OrgSlug}')", orgSlug);

            return Ok(await _organizationsManager.Get(orgSlug));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Organizations controller Get('{OrgSlug}')", orgSlug);

            return ExceptionResult(ex);
        }

    }

    /// <summary>
    /// Creates a new organization.
    /// </summary>
    /// <param name="organization">The organization data.</param>
    /// <returns>The newly created organization.</returns>
    [HttpPost(Name = nameof(Create))]
    [ProducesResponseType(typeof(OrganizationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromForm, Required] OrganizationDto organization)
    {

        try
        {
            _logger.LogDebug("Organizations controller Create('{organization?.Slug}')", organization?.Slug);

            var newOrg = await _organizationsManager.AddNew(organization);
            return CreatedAtRoute(nameof(Get), new { orgSlug = newOrg.Slug },
                newOrg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Organizations controller Create('{organization?.Slug}')", organization?.Slug);

            return ExceptionResult(ex);
        }

    }

    /// <summary>
    /// Updates an existing organization.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="organization">The organization update data.</param>
    /// <returns>No content on success.</returns>
    [HttpPut(RoutesHelper.OrganizationSlug, Name = nameof(Update))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        [FromRoute, Required] string orgSlug,
        [FromForm, Required] OrganizationDto organization)
    {

        try
        {
            _logger.LogDebug("Organizations controller Update('{OrgSlug}', {organization?.Slug}')",orgSlug, organization?.Slug);

            await _organizationsManager.Edit(orgSlug, organization);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Organizations controller Update('{OrgSlug}', {organization?.Slug}')", orgSlug, organization?.Slug);

            return ExceptionResult(ex);
        }

    }

    /// <summary>
    /// Deletes an organization.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete(RoutesHelper.OrganizationSlug, Name = nameof(Delete))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete([FromRoute, Required] string orgSlug)
    {

        try
        {
            _logger.LogDebug("Organizations controller Delete('{OrgSlug}')", orgSlug);

            await _organizationsManager.Delete(orgSlug);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Organizations controller Delete('{OrgSlug}')", orgSlug);

            return ExceptionResult(ex);
        }

    }

    #region Member Management

    /// <summary>
    /// Gets all members of an organization.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <returns>List of organization members with their permissions.</returns>
    [HttpGet("{orgSlug}/members", Name = nameof(GetMembers))]
    [ProducesResponseType(typeof(IEnumerable<OrganizationMemberDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMembers([FromRoute, Required] string orgSlug)
    {
        try
        {
            _logger.LogDebug("Organizations controller GetMembers('{OrgSlug}')", orgSlug);
            return Ok(await _organizationsManager.GetMembers(orgSlug));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Organizations controller GetMembers('{OrgSlug}')", orgSlug);
            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddMember(
        [FromRoute, Required] string orgSlug,
        [FromBody, Required] AddOrganizationMemberDto request)
    {
        try
        {
            _logger.LogDebug("Organizations controller AddMember('{OrgSlug}', '{UserName}')", orgSlug, request.UserName);
            await _organizationsManager.AddMember(orgSlug, request.UserName, request.Permissions);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Organizations controller AddMember('{OrgSlug}', '{UserName}')", orgSlug, request.UserName);
            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMemberPermission(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string userName,
        [FromBody, Required] UpdateMemberPermissionsDto request)
    {
        try
        {
            _logger.LogDebug("Organizations controller UpdateMemberPermission('{OrgSlug}', '{UserName}', {Permissions})",
                orgSlug, userName, request.Permissions);
            await _organizationsManager.UpdateMemberPermission(orgSlug, userName, request.Permissions);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Organizations controller UpdateMemberPermission('{OrgSlug}', '{UserName}')",
                orgSlug, userName);
            return ExceptionResult(ex);
        }
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
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMember(
        [FromRoute, Required] string orgSlug,
        [FromRoute, Required] string userName)
    {
        try
        {
            _logger.LogDebug("Organizations controller RemoveMember('{OrgSlug}', '{UserName}')", orgSlug, userName);
            await _organizationsManager.RemoveMember(orgSlug, userName);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Organizations controller RemoveMember('{OrgSlug}', '{UserName}')", orgSlug, userName);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets the organization member management feature status.
    /// </summary>
    /// <returns>Whether member management is enabled.</returns>
    [HttpGet("features/member-management", Name = nameof(GetMemberManagementStatus))]
    [AllowAnonymous]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult GetMemberManagementStatus()
    {
        return Ok(new { enabled = _organizationsManager.IsMemberManagementEnabled });
    }

    #endregion

}