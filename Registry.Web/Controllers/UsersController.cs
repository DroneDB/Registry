using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Attributes;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Users;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Controllers;

/// <summary>
/// Controller for managing users, authentication and roles.
/// </summary>
[Authorize]
[ApiController]
[Route(RoutesHelper.UsersRadix)]
[Produces("application/json")]
public class UsersController : ControllerBaseEx
{
    private readonly UsersManager _usersManager;
    private readonly AuthTokenOptions _tokens;
    private readonly NotificationStatus _notificationStatus;

    public UsersController(UsersManager usersManager, IOptions<AuthTokenOptions> tokens, NotificationStatus notificationStatus)
    {
        _usersManager = usersManager;
        _tokens = tokens.Value;
        _notificationStatus = notificationStatus;
    }

    /// <summary>
    /// Authenticates a user with username/password or token.
    /// </summary>
    /// <param name="model">The authentication request containing username/password or token.</param>
    /// <returns>The authentication response with JWT token.</returns>
    [AllowAnonymous]
    [HttpPost("authenticate", Name = nameof(Authenticate))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthenticateResponse>> Authenticate([FromForm] AuthenticateRequest model, CancellationToken ct)
    {
        if (model == null)
            throw new BadRequestException("No auth data provided");

        AuthenticateResponse? res;
        if (!string.IsNullOrWhiteSpace(model.Token))
            res = await _usersManager.Authenticate(model.Token);
        else if (model.Username == null || model.Password == null)
            throw new BadRequestException("No auth data provided");
        else
            res = await _usersManager.Authenticate(model.Username, model.Password);

        if (res == null)
            throw new UnauthorizedException("Unauthorized", noRetry: false);

        return res;
    }

    /// <summary>
    /// Expires the auth cookie. The server may set it <c>HttpOnly</c> (Basic auth), and then the web client
    /// cannot clear it from script: without this a signed-out browser would keep sending it. Anonymous, so an
    /// expired or invalid cookie can always be cleared; the token itself stays valid until it expires.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("logout", Name = nameof(Logout))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Logout()
    {
        // Same name and attributes (Path, Secure, SameSite) it was set with, or the browser keeps it; the options
        // validator refuses to start without a cookie name
        Response.Cookies.Delete(_tokens.AuthCookieName!, AuthCookieOptions.For(Request));
        return Ok();
    }

    /// <summary>
    /// Refreshes the current user's JWT token.
    /// </summary>
    /// <returns>The new authentication response with refreshed JWT token.</returns>
    [HttpPost("authenticate/refresh", Name = nameof(Refresh))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthenticateResponse>> Refresh(CancellationToken ct)
    {
        var res = await _usersManager.Refresh();

        if (res == null)
            throw new UnauthorizedException("Unauthorized", noRetry: false);

        return res;
    }

    /// <summary>
    /// Creates a new user. Requires admin privileges.
    /// </summary>
    /// <param name="model">The user creation request containing username, email, password and roles.</param>
    /// <returns>The created user information.</returns>
    [HttpPost(Name = nameof(CreateUser))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserRequest model, CancellationToken ct)
    {
        if (model == null)
            throw new BadRequestException("No user data provided");

        var res = await _usersManager.CreateUser(model.UserName, model.Email, model.Password, model.Roles);

        return res;
    }

    /// <summary>
    /// Changes the current user's password using form data.
    /// </summary>
    /// <param name="oldPassword">The current password.</param>
    /// <param name="newPassword">The new password.</param>
    /// <returns>The authentication response with new JWT token.</returns>
    [HttpPost("changepwd", Name = nameof(ChangePasswordPost))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthenticateResponse?>> ChangePasswordPost(
        [FromForm, Required] string oldPassword,
        [FromForm, Required] string newPassword, CancellationToken ct)
    {
        var res = await _usersManager.ChangePassword(oldPassword, newPassword);
        var auth = await _usersManager.Authenticate(res.UserName, res.Password);

        return auth;
    }

    /// <summary>
    /// Changes a user's password. Admins can change any user's password without the current password.
    /// </summary>
    /// <param name="userName">The username of the user whose password to change.</param>
    /// <param name="model">The password change request containing current and new password.</param>
    /// <returns>No content on success.</returns>
    [HttpPut("{userName}/changepwd", Name = nameof(ChangePassword))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword(
        [FromRoute, Required] string userName,
        [FromBody, Required] ChangeUserPasswordRequestDto model, CancellationToken ct)
    {
        if (model == null)
            throw new BadRequestException("No user data provided");

        await _usersManager.ChangePassword(userName, model.CurrentPassword, model.NewPassword);

        return Ok();
    }

    /// <summary>
    /// Deletes a user by username via route parameter. Requires admin privileges.
    /// Optionally transfers all user data to a successor user.
    /// </summary>
    /// <param name="userName">The username of the user to delete.</param>
    /// <param name="successor">Optional username of the successor to transfer data to.</param>
    /// <param name="conflictResolution">How to handle dataset naming conflicts when transferring.</param>
    /// <returns>The result of the deletion operation.</returns>
    [HttpDelete("{userName}", Name = nameof(DeleteUserRoute))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DeleteUserResultDto>> DeleteUserRoute(
        [FromRoute, Required] string userName,
        [FromQuery] string? successor = null,
        [FromQuery] ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.Rename, CancellationToken ct = default)
    {
        var result = await _usersManager.DeleteUser(userName, successor, conflictResolution);

        return result;
    }

    /// <summary>
    /// Gets all available roles in the system.
    /// </summary>
    /// <returns>An array of role names.</returns>
    [HttpGet("roles", Name = nameof(GetRoles))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<string[]>> GetRoles(CancellationToken ct)
    {
        var roles = await _usersManager.GetRoles(ct: ct);

        return roles;
    }

    /// <summary>
    /// Deletes a user by username via form data. Requires admin privileges.
    /// Optionally transfers all user data to a successor user.
    /// </summary>
    /// <param name="model">The delete user request containing username and optional successor.</param>
    /// <returns>The result of the deletion operation.</returns>
    [HttpDelete(Name = nameof(DeleteUser))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DeleteUserResultDto>> DeleteUser([FromForm, Required] DeleteUserDto model, CancellationToken ct)
    {
        if (model == null)
            throw new BadRequestException("No user data provided");

        var result = await _usersManager.DeleteUser(model.UserName, model.Successor, model.ConflictResolution);

        return result;
    }

    /// <summary>
    /// Gets all users in the system. Requires admin privileges.
    /// </summary>
    /// <returns>A list of all users.</returns>
    [HttpGet(Name = nameof(UsersController) + "." + nameof(GetAll))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetAll(CancellationToken ct)
    {
        var res = await _usersManager.GetAll(ct: ct);

        return Answer(res);
    }

    /// <summary>
    /// Gets all users with detailed information. Requires admin privileges.
    /// </summary>
    /// <returns>A list of all users with detailed information including storage usage.</returns>
    [HttpGet("detailed", Name = nameof(GetAllDetailed))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<UserDetailDto>>> GetAllDetailed(CancellationToken ct)
    {
        var res = await _usersManager.GetAllDetailed(ct: ct);

        return Answer(res);
    }

    /// <summary>
    /// Gets the current user's Hub preferences, one property per section ({} when none).
    /// </summary>
    /// <returns>The sections.</returns>
    [HttpGet("preferences", Name = nameof(GetPreferences))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<Dictionary<string, object>>> GetPreferences(CancellationToken ct) =>
        await _usersManager.GetPreferences();

    /// <summary>
    /// Sets the sections in the body and keeps the others; a section set to null is deleted (back to the Hub defaults).
    /// </summary>
    /// <param name="patch">The sections to set, by name.</param>
    /// <returns>All the sections after the change.</returns>
    [HttpPatch("preferences", Name = nameof(PatchPreferences))]
    [RequestSizeLimit(UserPreferences.MaxTotalBytes)]
    [EnforceContentLength(UserPreferences.MaxTotalBytes, "Preferences")]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<Dictionary<string, object>>> PatchPreferences(
        [FromBody, Required] Dictionary<string, JsonElement> patch, CancellationToken ct) =>
        await _usersManager.PatchPreferences(patch, ct);

    /// <summary>
    /// Whether the current user can get notification e-mails: <c>reason</c> is <c>emailDisabled</c> (the server sends
    /// no e-mail) or <c>noAddress</c> (the account has no valid address), null when they can.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("notifications/status", Name = nameof(GetNotificationStatus))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<NotificationStatusDto>> GetNotificationStatus(CancellationToken ct) =>
        await _notificationStatus.GetAsync(ct);

    /// <summary>
    /// Gets the current user: their name, e-mail, roles and the slugs of the organizations they own or belong to.
    /// </summary>
    /// <returns>The current user.</returns>
    [HttpGet("me", Name = nameof(GetCurrentUser))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserDto>> GetCurrentUser(CancellationToken ct) =>
        await _usersManager.GetCurrentUserProfile(ct);

    /// <summary>
    /// Gets the storage quota information for the current user.
    /// </summary>
    /// <returns>The user's storage information including total and used space.</returns>
    [HttpGet("storage", Name = nameof(GetCurrentUserStorageInfo))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserStorageInfo>> GetCurrentUserStorageInfo(CancellationToken ct)
    {
        var res = await _usersManager.GetUserStorageInfo();

        return res;
    }

    /// <summary>
    /// Gets the storage quota information for a specific user. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <returns>The user's storage information including total and used space.</returns>
    [HttpGet("{userName}/storage", Name = nameof(GetUserStorageInfo))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserStorageInfo>> GetUserStorageInfo([FromRoute, Required] string userName, CancellationToken ct)
    {
        var res = await _usersManager.GetUserStorageInfo(userName);

        return res;
    }

    /// <summary>
    /// Gets the metadata for the current user.
    /// </summary>
    /// <returns>A dictionary containing the user's metadata.</returns>
    [HttpGet("meta", Name = nameof(GetCurrentUserMeta))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<Dictionary<string, object>>> GetCurrentUserMeta(CancellationToken ct)
    {
        var meta = await _usersManager.GetUserMeta();

        return meta;
    }

    /// <summary>
    /// Gets the metadata for a specific user. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <returns>A dictionary containing the user's metadata.</returns>
    [HttpGet("{userName}/meta", Name = nameof(GetUserMeta))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<Dictionary<string, object>>> GetUserMeta([FromRoute, Required] string userName, CancellationToken ct)
    {
        var meta = await _usersManager.GetUserMeta(userName);

        return meta;
    }

    /// <summary>
    /// Sets the metadata for a specific user. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <param name="meta">A dictionary containing the metadata to set.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("{userName}/meta", Name = nameof(SetUserMeta))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetUserMeta(
        [FromRoute, Required] string userName,
        [FromBody, Required] Dictionary<string, object> meta, CancellationToken ct)
    {
        await _usersManager.SetUserMeta(userName, meta);

        return Ok();
    }

    #region Organizations

    /// <summary>
    /// Gets the organizations that a user belongs to, including membership details. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <returns>An array of organizations the user belongs to with membership info.</returns>
    [HttpGet("{userName}/orgs", Name = nameof(GetOrganizations))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UserOrganizationMembershipDto[]>> GetOrganizations([FromRoute, Required] string userName, CancellationToken ct)
    {
        var organizations = await _usersManager.GetUserOrganizations(userName, ct: ct);

        return organizations;
    }

    /// <summary>
    /// Adds a user to an organization with specified permissions. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <param name="request">The organization and permissions to assign.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("{userName}/orgs", Name = nameof(AddUserToOrganization))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AddUserToOrganization(
        [FromRoute, Required] string userName,
        [FromBody, Required] AddUserToOrganizationDto request, CancellationToken ct)
    {
        // Optional in the body (it was never [Required]), but there is no organization to join without it
        if (string.IsNullOrWhiteSpace(request.OrgSlug))
            throw new BadRequestException("Missing organization slug");

        await _usersManager.AddUserToOrganization(userName, request.OrgSlug, request.Permissions);

        return Ok();
    }

    /// <summary>
    /// Updates a user's permissions in an organization. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="request">The new permissions.</param>
    /// <returns>No content on success.</returns>
    [HttpPut("{userName}/orgs/{orgSlug}", Name = nameof(UpdateUserOrganizationPermissions))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateUserOrganizationPermissions(
        [FromRoute, Required] string userName,
        [FromRoute, Required] string orgSlug,
        [FromBody, Required] UpdateMemberPermissionsDto request, CancellationToken ct)
    {
        await _usersManager.UpdateUserOrganizationPermissions(userName, orgSlug, request.Permissions);

        return Ok();
    }

    /// <summary>
    /// Removes a user from an organization. Requires admin privileges. Cannot remove owner.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <param name="orgSlug">The organization slug.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("{userName}/orgs/{orgSlug}", Name = nameof(RemoveUserFromOrganization))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RemoveUserFromOrganization(
        [FromRoute, Required] string userName,
        [FromRoute, Required] string orgSlug, CancellationToken ct)
    {
        await _usersManager.RemoveUserFromOrganization(userName, orgSlug);

        return Ok();
    }

    #endregion

    /// <summary>
    /// Creates a new role. Requires admin privileges.
    /// </summary>
    /// <param name="request">The role creation request containing the role name.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("roles", Name = nameof(CreateRole))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateRole([FromBody, Required] CreateRoleRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RoleName))
            throw new BadRequestException("Role name is required");

        await _usersManager.CreateRole(request.RoleName);

        return Ok();
    }

    /// <summary>
    /// Deletes a role. Requires admin privileges. The admin role cannot be deleted.
    /// </summary>
    /// <param name="roleName">The name of the role to delete.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("roles/{roleName}", Name = nameof(DeleteRole))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteRole([FromRoute, Required] string roleName, CancellationToken ct)
    {
        await _usersManager.DeleteRole(roleName);

        return NoContent();
    }

    /// <summary>
    /// Updates a user's email and roles. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user to update.</param>
    /// <param name="request">The update request containing email and roles.</param>
    /// <returns>No content on success.</returns>
    [HttpPut("{userName}", Name = nameof(UpdateUser))]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateUser(
        [FromRoute, Required] string userName,
        [FromBody, Required] EditUserRequestDto request, CancellationToken ct)
    {
        await _usersManager.UpdateUser(userName, request.Email);
        await _usersManager.UpdateUserRoles(userName, request.Roles);

        return Ok();
    }
}

