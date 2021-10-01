using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using MimeMapping;
using Registry.Ports.ObjectSystem;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers
{
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

    [ApiController]
    [RestrictToS3]
    [RestrictToLocalhost]
    [Route(RoutesHelper.BridgeRadix)]
    public class S3BridgeController : ControllerBaseEx
    {
        private readonly IS3BridgeManager _s3BridgeManager;
        private readonly ILogger<S3BridgeController> _logger;
        private readonly IObjectSystem _objectSystem;

        public bool IsS3Enabled()
        {
            return _s3BridgeManager.IsS3Enabled();
        }

        public S3BridgeController(IS3BridgeManager s3BridgeManager, IObjectSystem objectSystem, ILogger<S3BridgeController> logger)
        {
            _s3BridgeManager = s3BridgeManager;
            _objectSystem = objectSystem;
            _logger = logger;
        }


        [HttpGet("{bucket}/{*path}", Name = nameof(S3BridgeController) + "." + nameof(Get))]
        public async Task<IActionResult> Get([FromRoute] string bucket, string path)
        {
            _logger.LogDebug($"S3Bridge controller Get('{bucket}', '{path}')");

            var fsr = new TaskCompletionSource<IActionResult>();

            if (Request.Headers.ContainsKey("Range"))
            {
                // TODO
            }
            else
            {
                // Executed asynchronously, we do not wait for this to complete
                // so that we can stream the result.
                _ = _objectSystem.GetObjectAsync(bucket, path, stream =>
                {
                    fsr.SetResult(File(stream, MimeUtility.GetMimeMapping(path), Path.GetFileName(path)));
                }).ContinueWith(t =>
                {
                    _logger.LogError(t.Exception, $"Exception in S3Bridge controller Get('{bucket}', '{path}')");
                    fsr.SetResult(ExceptionResult(t.Exception));
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
                
            return await fsr.Task;
        }
    }

}
