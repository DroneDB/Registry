using Microsoft.AspNetCore.Http;
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

        [HttpGet("version", Name = nameof(SystemController) + "." + nameof(GetVersion))]
        [ProducesResponseType(typeof(string), 200)]
        public IActionResult GetVersion()
        {
            try
            {
                _logger.LogDebug("System controller GetVersion()");

                return Ok(_systemManager.GetVersion());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in System controller GetVersion()");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("cleanupbatches", Name = nameof(SystemController) + "." + nameof(CleanupBatches))]
        public async Task<IActionResult> CleanupBatches()
        {
            try
            {
                _logger.LogDebug("System controller CleanupBatches()");

                return Ok(await _systemManager.CleanupBatches());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in System controller CleanupBatches()");

                return ExceptionResult(ex);
            }
        }


        [HttpPost("cleanupdatasets", Name = nameof(SystemController) + "." + nameof(CleanupDatasets))]
        public async Task<IActionResult> CleanupDatasets()
        {
            try
            {
                _logger.LogDebug("System controller CleanupDatasets()");

                return Ok(await _systemManager.CleanupEmptyDatasets());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in System controller CleanupDatasets()");

                return ExceptionResult(ex);
            }
        }
        
        [HttpPost("migratevisibility", Name = nameof(SystemController) + "." + nameof(MigrateVisibility))]
        public async Task<IActionResult> MigrateVisibility()
        {
            try
            {
                _logger.LogDebug("System controller MigrateVisibility()");

                return Ok(await _systemManager.MigrateVisibility());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in System controller CleanupDatasets()");

                return ExceptionResult(ex);
            }
        }

    }
}
