using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
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

namespace Registry.Web.Controllers
{
    [ApiController]
    public class StacController : ControllerBaseEx
    {
        private readonly IStacManager _stacManager;
        private readonly ILogger<StacController> _logger;

        public StacController(IStacManager stacManager,
            ILogger<StacController> logger)
        {
            _stacManager = stacManager;
            _logger = logger;
        }

        [HttpGet("/stac", Name = nameof(StacController) + "." + nameof(GetCatalog))]
        [ProducesResponseType(typeof(IEnumerable<StacCatalogDto>), 200)]
        public async Task<IActionResult> GetCatalog()
        {
            try
            {
                _logger.LogDebug("Stac controller GetCatalog()");

                return Ok(await _stacManager.GetCatalog());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Stac controller GetCatalog()");

                return ExceptionResult(ex);
            }
        }

        [HttpGet("/orgs/{orgSlug}/ds/{dsSlug}/stac/{pathBase64?}",
            Name = nameof(StacController) + "." + nameof(GetStacChild))]
        public async Task<IActionResult> GetStacChild([FromRoute] string orgSlug, [FromRoute] string dsSlug,
            [FromRoute] string pathBase64 = null)
        {
            try
            {
                _logger.LogDebug("Stac controller GetStacChild()");

                var path = pathBase64 != null ? Encoding.UTF8.GetString(Convert.FromBase64String(pathBase64)) : null;

                return Ok(await _stacManager.GetStacChild(orgSlug, dsSlug, path));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in Stac controller GetStacChild()");

                return ExceptionResult(ex);
            }
        }
    }
}