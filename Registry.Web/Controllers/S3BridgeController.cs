using System;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using MimeMapping;
using Registry.Ports.ObjectSystem;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers
{
    [ApiController]
    [RestrictToS3]
    [RestrictToLocalhost]
    [Route(RoutesHelper.BridgeRadix)]
    public class S3BridgeController : ControllerBaseEx
    {
        private readonly ILogger<S3BridgeController> _logger;
        private readonly IObjectSystem _objectSystem;

        [ApiExplorerSettings(IgnoreApi = true)]
        [NonAction]
        public bool IsS3Enabled()
        {
            return _objectSystem.IsS3Based();
        }

        public S3BridgeController(IObjectSystem objectSystem, ILogger<S3BridgeController> logger)
        {
            _objectSystem = objectSystem;
            _logger = logger;
        }

        [HttpHead("{bucket}/{*path}", Name = nameof(S3BridgeController) + "." + nameof(Check))]
        public async Task<IActionResult> Check([FromRoute] string bucket, string path)
        {
            _logger.LogDebug($"S3Bridge controller Check('{bucket}', '{path}')");
            try
            {
                if (!await _objectSystem.ObjectExistsAsync(bucket, path)) 
                    return NotFound();

                Response.Headers.Add("Accept-Ranges", "bytes");

                return Ok();

            }catch(Exception ex)
            {
                _logger.LogError(ex, $"Exception in S3 bridge controller Check('{bucket}', '{path}')");

                return ExceptionResult(ex);
            }
        }

        [HttpGet("{bucket}/{*path}", Name = nameof(S3BridgeController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string bucket, string path)
        {
            _logger.LogDebug($"S3Bridge controller Get('{bucket}', '{path}')");

            try
            {
                StreamableFileDescriptor res = null;

                if (Request.Headers.ContainsKey("Range"))
                {

                    var rangeHdr = RangeHeaderValue.Parse(Request.Headers["Range"]);
                    if (rangeHdr.Unit != "bytes") throw new ArgumentException("Only bytes units are supported in range");

                    foreach (var range in rangeHdr.Ranges)
                    {
                        long offset = range.From.Value;
                        long length = range.To.Value - range.From.Value;

                        res = new StreamableFileDescriptor(async (stream, cancellationToken) => {
                            await _objectSystem.GetObjectAsync(bucket, path, offset, length,
                                    source => source.CopyTo(stream), cancellationToken: cancellationToken);
                        }, Path.GetFileName(path), MimeUtility.GetMimeMapping(path));

                        break;
                    }
                }
                else
                {
                    res = new StreamableFileDescriptor(async (stream, cancellationToken) => {
                        await _objectSystem.GetObjectAsync(bucket, path,
                                source => source.CopyTo(stream), cancellationToken: cancellationToken);
                    }, Path.GetFileName(path), MimeUtility.GetMimeMapping(path));
                }

                Response.StatusCode = 200;
                Response.ContentType = res.ContentType;
                await res.CopyToAsync(Response.Body);

                return new EmptyResult();
            }catch (Exception ex){
                _logger.LogError(ex, $"Exception in S3Bridge controller Get('{bucket}', '{path}')");
                return ExceptionResult(ex);
            }
        }
    }

    public class RestrictToS3Attribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var controller = (S3BridgeController)context.Controller;
            if (!controller.IsS3Enabled())
            {
                context.Result = new NotFoundResult();
                return;
            }
            base.OnActionExecuting(context);
        }
    }

    public class RestrictToLocalhostAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var remoteIp = context.HttpContext.Connection.RemoteIpAddress;
            if (!IPAddress.IsLoopback(remoteIp))
            {
                context.Result = new UnauthorizedResult();
                return;
            }
            base.OnActionExecuting(context);
        }
    }

}
