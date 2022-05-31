using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Registry.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route(RoutesHelper.UsersRadix)]
    public class UsersController : ControllerBaseEx
    {
        private readonly IUsersManager _usersManager;
        private readonly ILogger<UsersController> _logger;

        public UsersController(IUsersManager usersManager, ILogger<UsersController> logger)
        {
            _usersManager = usersManager;
            _logger = logger;
        }

        [AllowAnonymous]
        [HttpPost("authenticate")]
        [ProducesResponseType(typeof(AuthenticateResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> Authenticate([FromForm] AuthenticateRequest model)
        {
            try
            {
                _logger.LogDebug("Users controller Authenticate('{Username}')", model?.Username);

                if (model == null)
                    return BadRequest(new ErrorResponse("No auth data provided"));

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

        [HttpPost("authenticate/refresh")]
        [ProducesResponseType(typeof(AuthenticateResponse), StatusCodes.Status200OK)]
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

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> CreateUser([FromForm] CreateUserRequest model)
        {
            try
            {
                _logger.LogDebug("Users controller CreateUser('{UserName}', '{Email}')", model?.UserName, model?.Email);

                if (model == null)
                    return BadRequest(new ErrorResponse("No user data provided"));

                await _usersManager.CreateUser(model.UserName, model.Email, model.Password, model.Roles);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Users controller CreateUser('{UserName}', '{Email}')",
                    model?.UserName, model?.Email);

                return ExceptionResult(ex);
            }
        }

        [HttpPost("changepwd")]
        [ProducesResponseType(typeof(AuthenticateResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> ChangePasswordPost([FromForm] string oldPassword,
            [FromForm] string newPassword)
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

        [HttpPut]
        public async Task<IActionResult> ChangePassword([FromForm] ChangeUserPasswordRequestDto model)
        {
            try
            {
                _logger.LogDebug("Users controller ChangePassword('{UserName}')", model?.UserName);

                if (model == null)
                    return BadRequest(new ErrorResponse("No user data provided"));

                await _usersManager.ChangePassword(model.UserName, model.CurrentPassword, model.NewPassword);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Users controller ChangePassword('{UserName}')", model?.UserName);

                return ExceptionResult(ex);
            }
        }

        [HttpDelete("{userName}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> DeleteUserRoute([FromForm] string userName)
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

        [HttpGet("roles")]
        [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
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

        [HttpDelete]
        public async Task<IActionResult> DeleteUser([FromRoute] string userName)
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

        [HttpGet]
        [ProducesResponseType(typeof(IEnumerable<UserDto>), 200)]
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

        [HttpGet("storage")]
        [ProducesResponseType(typeof(UserStorageInfo), 200)]
        public async Task<IActionResult> GetUserQuotaInfo()
        {
            try
            {
                _logger.LogDebug("Users controller GetUserQuotaInfo()");

                var res = await _usersManager.GetUserStorageInfo();

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Users controller GetUserQuotaInfo()");

                return ExceptionResult(ex);
            }
        }

        [HttpGet("{userName}/storage")]
        [ProducesResponseType(typeof(UserStorageInfo), 200)]
        public async Task<IActionResult> GetUserQuotaInfo([FromRoute] string userName)
        {
            try
            {
                _logger.LogDebug("Users controller GetUserQuotaInfo('{UserName}')", userName);

                var res = await _usersManager.GetUserStorageInfo(userName);

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Users controller GetUserQuotaInfo('{UserName}')", userName);

                return ExceptionResult(ex);
            }
        }

        [HttpGet("meta")]
        [ProducesResponseType(typeof(Dictionary<string, object>), 200)]
        public async Task<IActionResult> GetUserMeta()
        {
            try
            {
                _logger.LogDebug("Users controller GetUserMeta()");

                var meta = await _usersManager.GetUserMeta();

                return Ok(meta);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Users controller GetUserMeta()");

                return ExceptionResult(ex);
            }
        }

        [HttpGet("{userName}/meta")]
        [ProducesResponseType(typeof(Dictionary<string, object>), 200)]
        public async Task<IActionResult> GetUserMeta([FromRoute] string userName)
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

        [HttpPost("{userName}/meta")]
        public async Task<IActionResult> SetUserMeta([FromRoute] string userName,
            [FromBody] Dictionary<string, object> meta)
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
    }
}