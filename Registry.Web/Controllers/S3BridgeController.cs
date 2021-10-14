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
        private readonly ILogger<S3BridgeController> _logger;
        private readonly IObjectSystem _objectSystem;
        private readonly ObjectCache _objectCache;

        private readonly TimeSpan _cacheExpiration;
        private readonly TimeSpan DefaultBridgeCacheExpiration = new TimeSpan(0, 30, 0);

        [ApiExplorerSettings(IgnoreApi = true)]
        [NonAction]
        public bool IsS3Enabled()
        {
            return _objectSystem.IsS3Based();
        }

        public S3BridgeController(IObjectSystem objectSystem, ObjectCache objectCache, IOptions<AppSettings> settings,
            ILogger<S3BridgeController> logger)
        {
            _objectSystem = objectSystem;
            _objectCache = objectCache;
            _logger = logger;
            
            _cacheExpiration = settings.Value.BridgeCacheExpiration ?? DefaultBridgeCacheExpiration;
        }

        [HttpHead(Name = nameof(S3BridgeController) + "." + nameof(Check))]
        public async Task<IActionResult> Check([FromRoute] string bucket, [FromRoute] string path)
        {
            _logger.LogDebug($"S3Bridge controller Check('{bucket}', '{path}')");

            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException("Path cannot be empty", nameof(Path));

                if (!await _objectSystem.ObjectExistsAsync(bucket, path))
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

                    await _objectSystem.GetObjectAsync(bucket, path, offset, length,
                        source =>
                        {
                            Response.StatusCode = 200;
                            Response.ContentType = MimeUtility.GetMimeMapping(path);
                            source.CopyTo(Response.Body);
                        });

                    return new EmptyResult();

                }

                // We should only cache files inside .build, otherwise we can absolutely shoot ourselves in the foot
                var key = $"Obj-{bucket}-{path}";

                var itm = _objectCache.Get(key);
                string filePath;
                
                if (itm != null)
                {
                    filePath = (string)itm;
                }
                else
                {
                    var tmpFile = Path.GetTempFileName();
                    try
                    {
                        await _objectSystem.GetObjectAsync(bucket, path, tmpFile);
                        _objectCache.Set(key, tmpFile, new CacheItemPolicy { SlidingExpiration = _cacheExpiration});
                    }
                    finally
                    {
                        CommonUtils.SafeDelete(tmpFile);
                    }

                    filePath = (string)_objectCache.Get(key);
                }

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