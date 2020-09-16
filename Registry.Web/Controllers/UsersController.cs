﻿using System;
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

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route("[controller]")]
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

                var res = await _usersManager.Authenticate(model);

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

    }
}
