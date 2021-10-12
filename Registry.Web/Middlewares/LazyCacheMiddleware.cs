using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;
using Minio.Exceptions;
using Registry.Common;
using Registry.Common.Exceptions;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Middlewares
{
    public class LazyCacheMiddleware : IMiddleware
    {
        private readonly IObjectsManager _objectsManager;
        private readonly AppSettings _settings;

        private const string baseUrl = "static";
        private const string thumbsBase = "thumbs";
        private const string tilesBase = "tiles";

        private static readonly Regex urlMatcher =
            new(baseUrl + @"\/" + thumbsBase + @"\/(?<org>[^\/]+)\/(?<ds>[^\/]+)\/(?<size>\d+)\/(?<path>.*)",
                RegexOptions.Singleline);

        public LazyCacheMiddleware(IObjectsManager objectsManager, IOptions<AppSettings> options)
        {
            _objectsManager = objectsManager;
            _settings = options.Value;
        }

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if ((context.Request.Path.Value == null) ||
                (context.Response.StatusCode != 404 && !context.Request.Path.Value.StartsWith("/" + baseUrl)))
            {
                await next(context);
                return;
            }

            var match = urlMatcher.Match(HttpUtility.UrlDecode(context.Request.Path));

            if (!match.Success)
            {
                await next(context);
                return;
            }

            var org = match.Groups["org"].Value;
            var ds = match.Groups["ds"].Value;
            var size = int.Parse(match.Groups["size"].Value);
            var path = match.Groups["path"].Value;

            var destPath = CommonUtils.SafeCombine(_settings.StaticFilesCachePath, thumbsBase, org, ds,
                size.ToString(), path);
            var signalPath = destPath + ".sig";

            try
            {

                // Create full path
                var folderName = Path.GetDirectoryName(destPath);
                if (folderName != null) Directory.CreateDirectory(folderName);

                // Prevent multiple generations (concurrency is our enemy)
                if (File.Exists(destPath))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "image/jpeg";
                    await using var fs = File.OpenRead(destPath);
                    await fs.CopyToAsync(context.Response.Body);
                    return;
                }

                // A bit jankie but effective: signalPath is set when we are generating it
                if (File.Exists(signalPath))
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.Response.Headers.Add("Retry-After", "10");
                    return;
                }

                await File.WriteAllTextAsync(signalPath, DateTime.Now.ToString("yyyyMMddHHmmssffff"));

                var thumb = await _objectsManager.GenerateThumbnail(org, ds, path, size);

                // Write content
                await using (var file = File.OpenWrite(destPath))
                    await thumb.ContentStream.CopyToAsync(file);

                thumb.ContentStream.Reset();

                // Return it
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = thumb.ContentType;
                await thumb.ContentStream.CopyToAsync(context.Response.Body);

                thumb.ContentStream.Close();
            }
            catch (IOException ex)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.Add("Retry-After", "10");
            }
            catch (ArgumentException ex)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new Registry.Web.Models.ErrorResponse(ex.Message));
            }
            catch (UnauthorizedException ex)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new Registry.Web.Models.ErrorResponse(ex.Message));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new Registry.Web.Models.ErrorResponse(ex.Message));
            }
            finally
            {
                CommonUtils.SafeDelete(signalPath);
            }
        }
    }
}