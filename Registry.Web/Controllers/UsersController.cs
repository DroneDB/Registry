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
        public async Task<IActionResult> Authenticate([FromForm] AuthenticateRequest model)
        {

            try
            {
                _logger.LogDebug($"Users controller Authenticate('{model.Username}')");

                var res = string.IsNullOrWhiteSpace(model.Token) ? 
                    await _usersManager.Authenticate(model.Username, model.Password) :
                    await _usersManager.Authenticate(model.Token);

                if (res == null)
                    return Unauthorized(new ErrorResponse("Unauthorized"));

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Users controller Authenticate('{model.Username}')");

                return ExceptionResult(ex);
            }

        }
        
        [HttpPost("authenticate/refresh")]
        public async Task<IActionResult> Refresh()
        {

            try
            {
                _logger.LogDebug($"Users controller Refresh()");

                var res = await _usersManager.Refresh();

                if (res == null)
                    return Unauthorized(new ErrorResponse("Unauthorized"));

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Users controller Refresh()");

                return ExceptionResult(ex);
            }

        }

        [HttpPost]
        public async Task<IActionResult> CreateUser([FromForm] CreateUserRequest model)
        {

            try
            {
                _logger.LogDebug($"Users controller CreateUser('{model.UserName}', '{model.Email}')");

                await _usersManager.CreateUser(model.UserName, model.Email, model.Password);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Users controller CreateUser()");

                return ExceptionResult(ex);
            }

        }

        [HttpPut]
        public async Task<IActionResult> ChangePassword([FromForm] ChangeUserPasswordRequest model)
        {

            try
            {
                _logger.LogDebug($"Users controller ChangePassword('{model.UserName}')");

                await _usersManager.ChangePassword(model.UserName, model.CurrentPassword, model.NewPassword);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Users controller ChangePassword()");

                return ExceptionResult(ex);
            }

        }

        [HttpDelete]
        public async Task<IActionResult> DeleteUser([FromForm] string userName)
        {

            try
            {
                _logger.LogDebug($"Users controller DeleteUser('{userName}')");

                await _usersManager.DeleteUser(userName);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Users controller DeleteUser()");

                return ExceptionResult(ex);
            }

        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {

            try
            {
                _logger.LogDebug($"Users controller GetAll()");

                var res = await _usersManager.GetAll();

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Users controller GetAll()");

                return ExceptionResult(ex);
            }

        }

        [HttpGet("storage")]
        public async Task<IActionResult> GetUserQuotaInfo()
        {
            try
            {
                _logger.LogDebug($"Users controller GetUserQuotaInfo()");

                var res = await _usersManager.GetUserStorageInfo();

                return Ok(res);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Users controller GetUserQuotaInfo()");

                return ExceptionResult(ex);
            }

        }

        [HttpGet("meta")]
        public async Task<IActionResult> GetUserMeta()
        {

            try
            {
                _logger.LogDebug($"Users controller GetUserMeta()");

                var meta = await _usersManager.GetUserMeta();

                return Ok(meta);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Users controller GetUserMeta()");

                return ExceptionResult(ex);
            }

        }

        [HttpPost("meta")]
        public async Task<IActionResult> SetUserMeta([FromBody]Dictionary<string, object> meta)
        {

            try
            {
                _logger.LogDebug($"Users controller SetUserMeta()");

                await _usersManager.SetUserMeta(meta);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in Users controller SetUserMeta()");

                return ExceptionResult(ex);
            }

        }

    }
}
