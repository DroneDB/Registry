using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

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
    private readonly IUsersManager _usersManager;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IUsersManager usersManager, ILogger<UsersController> logger)
    {
        _usersManager = usersManager;
        _logger = logger;
    }

    /// <summary>
    /// Authenticates a user with username/password or token.
    /// </summary>
    /// <param name="model">The authentication request containing username/password or token.</param>
    /// <returns>The authentication response with JWT token.</returns>
    [AllowAnonymous]
    [HttpPost("authenticate", Name = nameof(Authenticate))]
    [ProducesResponseType(typeof(AuthenticateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Authenticate([FromForm] AuthenticateRequest model)
    {
        try
        {
            if (model == null)
                return BadRequest(new ErrorResponse("No auth data provided"));

            _logger.LogDebug("Users controller Authenticate('{Username}')", model.Username);
            
            var res = string.IsNullOrWhiteSpace(model.Token)
                ? await _usersManager.Authenticate(model.Username, model.Password)
                : await _usersManager.Authenticate(model.Token);

            if (res == null)
                return Unauthorized(new ErrorResponse("Unauthorized"));

            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller Authenticate('{Username}')", model?.Username);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Refreshes the current user's JWT token.
    /// </summary>
    /// <returns>The new authentication response with refreshed JWT token.</returns>
    [HttpPost("authenticate/refresh", Name = nameof(Refresh))]
    [ProducesResponseType(typeof(AuthenticateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh()
    {
        try
        {
            _logger.LogDebug("Users controller Refresh()");

            var res = await _usersManager.Refresh();

            if (res == null)
                return Unauthorized(new ErrorResponse("Unauthorized"));

            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller Refresh()");
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Creates a new user. Requires admin privileges.
    /// </summary>
    /// <param name="model">The user creation request containing username, email, password and roles.</param>
    /// <returns>The created user information.</returns>
    [HttpPost(Name = nameof(CreateUser))]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest model)
    {
        try
        {
            _logger.LogDebug("Users controller CreateUser('{UserName}', '{Email}')", model?.UserName, model?.Email);

            if (model == null)
                return BadRequest(new ErrorResponse("No user data provided"));

            var res = await _usersManager.CreateUser(model.UserName, model.Email, model.Password, model.Roles);

            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller CreateUser('{UserName}', '{Email}')",
                model?.UserName, model?.Email);

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Changes the current user's password using form data.
    /// </summary>
    /// <param name="oldPassword">The current password.</param>
    /// <param name="newPassword">The new password.</param>
    /// <returns>The authentication response with new JWT token.</returns>
    [HttpPost("changepwd", Name = nameof(ChangePasswordPost))]
    [ProducesResponseType(typeof(AuthenticateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePasswordPost(
        [FromForm, Required] string oldPassword,
        [FromForm, Required] string newPassword)
    {
        try
        {
            _logger.LogDebug("Users controller ChangePasswordPost('XXX','XXX')");

            var res = await _usersManager.ChangePassword(oldPassword, newPassword);
            var auth = await _usersManager.Authenticate(res.UserName, res.Password);

            return Ok(auth);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller ChangePasswordPost('XXX','XXX')");

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Changes a user's password. Admins can change any user's password without the current password.
    /// </summary>
    /// <param name="userName">The username of the user whose password to change.</param>
    /// <param name="model">The password change request containing current and new password.</param>
    /// <returns>No content on success.</returns>
    [HttpPut("{userName}/changepwd", Name = nameof(ChangePassword))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword(
        [FromRoute, Required] string userName,
        [FromBody, Required] ChangeUserPasswordRequestDto model)
    {
        try
        {
            _logger.LogDebug("Users controller ChangePassword('{UserName}')", userName);

            if (model == null)
                return BadRequest(new ErrorResponse("No user data provided"));

            await _usersManager.ChangePassword(userName, model.CurrentPassword, model.NewPassword);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller ChangePassword('{UserName}')", userName);

            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Deletes a user by username via route parameter. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user to delete.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("{userName}", Name = nameof(DeleteUserRoute))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteUserRoute([FromRoute, Required] string userName)
    {
        try
        {
            _logger.LogDebug("Users controller DeleteUserRoute('{UserName}')", userName);

            await _usersManager.DeleteUser(userName);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller DeleteUserRoute('{UserName}')", userName);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets all available roles in the system.
    /// </summary>
    /// <returns>An array of role names.</returns>
    [HttpGet("roles", Name = nameof(GetRoles))]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRoles()
    {
        try
        {
            _logger.LogDebug("Users controller GetRoles()");

            var roles = await _usersManager.GetRoles();

            return Ok(roles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Users controller GetRoles()");
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Deletes a user by username via form data. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user to delete.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete(Name = nameof(DeleteUser))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteUser([FromForm, Required] string userName)
    {
        try
        {
            _logger.LogDebug("Users controller DeleteUser('{UserName}')", userName);

            await _usersManager.DeleteUser(userName);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller DeleteUser('{UserName}')", userName);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets all users in the system. Requires admin privileges.
    /// </summary>
    /// <returns>A list of all users.</returns>
    [HttpGet(Name = nameof(UsersController) + "." + nameof(GetAll))]
    [ProducesResponseType(typeof(IEnumerable<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAll()
    {
        try
        {
            _logger.LogDebug("Users controller GetAll()");

            var res = await _usersManager.GetAll();

            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller GetAll()");
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets all users with detailed information. Requires admin privileges.
    /// </summary>
    /// <returns>A list of all users with detailed information including storage usage.</returns>
    [HttpGet("detailed", Name = nameof(GetAllDetailed))]
    [ProducesResponseType(typeof(IEnumerable<UserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAllDetailed()
    {
        try
        {
            _logger.LogDebug("Users controller GetAllDetailed()");

            var res = await _usersManager.GetAllDetailed();

            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller GetAllDetailed()");
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets the storage quota information for the current user.
    /// </summary>
    /// <returns>The user's storage information including total and used space.</returns>
    [HttpGet("storage", Name = nameof(GetCurrentUserStorageInfo))]
    [ProducesResponseType(typeof(UserStorageInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUserStorageInfo()
    {
        try
        {
            _logger.LogDebug("Users controller GetCurrentUserStorageInfo()");

            var res = await _usersManager.GetUserStorageInfo();

            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller GetCurrentUserStorageInfo()");
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets the storage quota information for a specific user. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <returns>The user's storage information including total and used space.</returns>
    [HttpGet("{userName}/storage", Name = nameof(GetUserStorageInfo))]
    [ProducesResponseType(typeof(UserStorageInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUserStorageInfo([FromRoute, Required] string userName)
    {
        try
        {
            _logger.LogDebug("Users controller GetUserStorageInfo('{UserName}')", userName);

            var res = await _usersManager.GetUserStorageInfo(userName);

            return Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller GetUserStorageInfo('{UserName}')", userName);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets the metadata for the current user.
    /// </summary>
    /// <returns>A dictionary containing the user's metadata.</returns>
    [HttpGet("meta", Name = nameof(GetCurrentUserMeta))]
    [ProducesResponseType(typeof(Dictionary<string, object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUserMeta()
    {
        try
        {
            _logger.LogDebug("Users controller GetCurrentUserMeta()");

            var meta = await _usersManager.GetUserMeta();

            return Ok(meta);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller GetCurrentUserMeta()");
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Gets the metadata for a specific user. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <returns>A dictionary containing the user's metadata.</returns>
    [HttpGet("{userName}/meta", Name = nameof(GetUserMeta))]
    [ProducesResponseType(typeof(Dictionary<string, object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUserMeta([FromRoute, Required] string userName)
    {
        try
        {
            _logger.LogDebug("Users controller GetUserMeta('{UserName}')", userName);

            var meta = await _usersManager.GetUserMeta(userName);

            return Ok(meta);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller GetUserMeta('{UserName}')", userName);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Sets the metadata for a specific user. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <param name="meta">A dictionary containing the metadata to set.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("{userName}/meta", Name = nameof(SetUserMeta))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetUserMeta(
        [FromRoute, Required] string userName,
        [FromBody, Required] Dictionary<string, object> meta)
    {
        try
        {
            _logger.LogDebug("Users controller SetUserMeta('{UserName}')", userName);

            await _usersManager.SetUserMeta(userName, meta);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller SetUserMeta('{UserName}')", userName);
            return ExceptionResult(ex);
        }
    }

    #region Organizations

    /// <summary>
    /// Gets the organizations that a user belongs to. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <returns>An array of organizations the user belongs to.</returns>
    [HttpGet("{userName}/orgs", Name = nameof(GetOrganizations))]
    [ProducesResponseType(typeof(OrganizationDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetOrganizations([FromRoute, Required] string userName)
    {
        try
        {
            _logger.LogDebug("Users controller GetOrganizations('{UserName}')", userName);

            var organizations = await _usersManager.GetUserOrganizations(userName);

            return Ok(organizations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller GetOrganizations('{UserName}')", userName);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Sets the organizations that a user belongs to. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user.</param>
    /// <param name="orgSlugs">An array of organization slugs to assign to the user.</param>
    /// <returns>No content on success.</returns>
    [HttpPut("{userName}/orgs", Name = nameof(SetUserOrganizations))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetUserOrganizations(
        [FromRoute, Required] string userName,
        [FromForm, Required] string[] orgSlugs)
    {
        try
        {
            _logger.LogDebug("Users controller SetUserOrganizations('{UserName}')", userName);

            await _usersManager.SetUserOrganizations(userName, orgSlugs);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller SetUserOrganizations('{UserName}')", userName);
            return ExceptionResult(ex);
        }
    }

    #endregion

    /// <summary>
    /// Checks if user management is enabled. User management is disabled when external authentication is configured.
    /// </summary>
    /// <returns>True if user management is enabled, false otherwise.</returns>
    [AllowAnonymous]
    [HttpGet("management-enabled", Name = nameof(IsUserManagementEnabled))]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public IActionResult IsUserManagementEnabled()
    {
        try
        {
            _logger.LogDebug("Users controller IsUserManagementEnabled()");

            // User management is disabled if an external authentication URL is configured
            var appSettings = HttpContext.RequestServices.GetRequiredService<IOptions<AppSettings>>().Value;
            var isEnabled = string.IsNullOrWhiteSpace(appSettings?.ExternalAuthUrl);

            return Ok(isEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller IsUserManagementEnabled()");
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Creates a new role. Requires admin privileges.
    /// </summary>
    /// <param name="request">The role creation request containing the role name.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("roles", Name = nameof(CreateRole))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateRole([FromBody, Required] CreateRoleRequestDto request)
    {
        try
        {
            _logger.LogDebug("Users controller CreateRole('{RoleName}')", request.RoleName);

            if (string.IsNullOrWhiteSpace(request.RoleName))
                return BadRequest(new ErrorResponse("Role name is required"));

            await _usersManager.CreateRole(request.RoleName);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller CreateRole('{RoleName}')", request.RoleName);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Deletes a role. Requires admin privileges. The admin role cannot be deleted.
    /// </summary>
    /// <param name="roleName">The name of the role to delete.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("roles/{roleName}", Name = nameof(DeleteRole))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteRole([FromRoute, Required] string roleName)
    {
        try
        {
            _logger.LogDebug("Users controller DeleteRole('{RoleName}')", roleName);

            await _usersManager.DeleteRole(roleName);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller DeleteRole('{RoleName}')", roleName);
            return ExceptionResult(ex);
        }
    }

    /// <summary>
    /// Updates a user's email and roles. Requires admin privileges.
    /// </summary>
    /// <param name="userName">The username of the user to update.</param>
    /// <param name="request">The update request containing email and roles.</param>
    /// <returns>No content on success.</returns>
    [HttpPut("{userName}", Name = nameof(UpdateUser))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateUser(
        [FromRoute, Required] string userName,
        [FromBody, Required] EditUserRequestDto request)
    {
        try
        {
            _logger.LogDebug("Users controller UpdateUser('{UserName}')", userName);

            await _usersManager.UpdateUser(userName, request.Email);
            await _usersManager.UpdateUserRoles(userName, request.Roles);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in Users controller UpdateUser('{UserName}')", userName);
            return ExceptionResult(ex);
        }
    }
}

