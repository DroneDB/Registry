using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    [Route(RoutesHelper.StacRadix)]
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
        
        [HttpGet(Name = nameof(StacController) + "." + nameof(GetCatalog))]
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

    }

}