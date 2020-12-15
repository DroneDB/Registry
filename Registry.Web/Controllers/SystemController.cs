﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers
{
    [Authorize]
    [ApiController]
    [Route(RoutesHelper.SystemRadix)]
    public class SystemController : ControllerBaseEx
    {
        private readonly ISystemManager _systemManager;
        private readonly ILogger<SystemController> _logger;

        public SystemController(ISystemManager systemManager, ILogger<SystemController> logger)
        {
            _systemManager = systemManager;
            _logger = logger;
        }

        [HttpGet("cleanupsessions", Name = nameof(SystemController) + "." + nameof(CleanupSessions))]
        public async Task<IActionResult> CleanupSessions()
        {
            try
            {
                _logger.LogDebug($"System controller CleanupSessions()");

                return Ok(await _systemManager.CleanupSessions());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in System controller CleanupSessions()");

                return ExceptionResult(ex);
            }
        }
    }
}