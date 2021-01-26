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

        [HttpPost("cleanupsessions", Name = nameof(SystemController) + "." + nameof(CleanupSessions))]
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

        [HttpPost("cleanupbatches", Name = nameof(SystemController) + "." + nameof(CleanupBatches))]
        public async Task<IActionResult> CleanupBatches()
        {
            try
            {
                _logger.LogDebug($"System controller CleanupBatches()");

                return Ok(await _systemManager.CleanupBatches());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in System controller CleanupBatches()");

                return ExceptionResult(ex);
            }
        }


        [HttpPost("syncddb", Name = nameof(SystemController) + "." + nameof(SyncDdbMeta))]
        public async Task<IActionResult> SyncDdbMeta(string[] orgs)
        {
            try
            {
                _logger.LogDebug($"System controller SyncDdbMeta({orgs.ToPrintableList()})");

                await _systemManager.SyncDdbMeta(orgs);

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in System controller SyncDdbMeta({orgs.ToPrintableList()})");

                return ExceptionResult(ex);
            }
        }

        [HttpPost("syncfiles", Name = nameof(SystemController) + "." + nameof(SyncFiles))]
        public IActionResult SyncFiles()
        {
            try
            {
                _logger.LogDebug($"System controller SyncFiles()");

                return Ok(_systemManager.SyncFiles());

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in System controller SyncFiles()");

                return ExceptionResult(ex);
            }
        }
    }
}
