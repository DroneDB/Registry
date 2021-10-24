using System;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeMapping;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.ObjectSystem;
using Registry.Web.Attributes;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers
{
    [ApiController]
    [RestrictToS3]
    [RestrictToLocalhost]
    [Route(RoutesHelper.BridgeRadix + "/{bucket}/{*path}")]
    public class S3BridgeController : ControllerBaseEx
    {
        private readonly IS3BridgeManager _bridgeManager;
        private readonly ILogger<S3BridgeController> _logger;

        public S3BridgeController(IS3BridgeManager bridgeManager, ILogger<S3BridgeController> logger)
        {
            _bridgeManager = bridgeManager;
            _logger = logger;
        }

        [ApiExplorerSettings(IgnoreApi = true)]
        [NonAction]
        public bool IsS3Enabled()
        {
            return _bridgeManager.IsS3Based();
        }
        
        [HttpHead(Name = nameof(S3BridgeController) + "." + nameof(Check))]
        public async Task<IActionResult> Check([FromRoute] string bucket, [FromRoute] string path)
        {
            _logger.LogDebug($"S3Bridge controller Check('{bucket}', '{path}')");

            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException("Path cannot be empty", nameof(Path));

                if (!await _bridgeManager.ObjectExists(bucket, path))
                    return NotFound();

                Response.Headers.Add("Accept-Ranges", "bytes");

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in S3 bridge controller Check('{bucket}', '{path}')");

                return ExceptionResult(ex);
            }
        }

        [HttpGet(Name = nameof(S3BridgeController) + "." + nameof(Get))]
        [ResponseCache(Duration = 60)]
        public async Task<IActionResult> Get([FromRoute] string bucket, [FromRoute] string path)
        {
            _logger.LogDebug($"S3Bridge controller Get('{bucket}', '{path}')");

            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException("Path cannot be empty", nameof(Path));

                if (Request.Headers.TryGetValue("Range", out var rangeHeaderRaw))
                {
                    var rangeHeader = RangeHeaderValue.Parse(rangeHeaderRaw);

                    if (rangeHeader.Unit != "bytes")
                        throw new ArgumentException("Only bytes units are supported in range");

                    if (rangeHeader.Ranges.Count > 1)
                        throw new ArgumentException("Multiple ranges are not supported");

                    var range = rangeHeader.Ranges.First();

                    if (!range.From.HasValue)
                        throw new ArgumentException("Range 'From' field is empty");

                    if (!range.To.HasValue)
                        throw new ArgumentException("Range 'To' field is empty");

                    var offset = range.From.Value;
                    var length = range.To.Value - range.From.Value;

                    await _bridgeManager.GetObjectStream(bucket, path, offset, length,
                        source =>
                        {
                            Response.StatusCode = 200;
                            Response.ContentType = MimeUtility.GetMimeMapping(path);
                            source.CopyTo(Response.Body);
                        });

                    return new EmptyResult();

                }

                var filePath = await _bridgeManager.GetObject(bucket, path);

                return PhysicalFile(Path.GetFullPath(filePath), MimeUtility.GetMimeMapping(path));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception in S3Bridge controller Get('{bucket}', '{path}')");
                return ExceptionResult(ex);
            }
        }
    }
}