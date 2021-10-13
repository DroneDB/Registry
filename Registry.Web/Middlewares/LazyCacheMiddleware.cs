using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Middlewares
{
    public class LazyCacheMiddleware : IMiddleware
    {
        private readonly IObjectsManager _objectsManager;
        private readonly AppSettings _settings;

        private const string baseUrl = "static";
        private const string thumbsBase = "thumbs";
        private const string tilesBase = "tiles";

        private static readonly Regex thumbsRegex =
            new(baseUrl + @"\/" + thumbsBase + @"\/(?<org>[^\/]+)\/(?<ds>[^\/]+)\/(?<size>\d+)\/(?<path>.*)",
                RegexOptions.Singleline);

        private static readonly Regex tilesRegex =
            new(baseUrl + @"\/" + tilesBase + @"\/(?<org>[^\/]+)\/(?<ds>[^\/]+)\/(?<tz>\d+)\/(?<tx>\d+)\/(?<ty>\d+)\/(?<retina>(1|0))\/(?<path>.+)\.png",
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

            var decodedPath = HttpUtility.UrlDecode(context.Request.Path);

            var match = thumbsRegex.Match(decodedPath);

            if (match.Success)
            {
                await ProcessThumbnail(context, match);
                return;
            }

            match = tilesRegex.Match(decodedPath);

            if (match.Success)
            {
                await ProcessTile(context, match);
                return;
            }

            await next(context);
        }

        private async Task ProcessTile(HttpContext context, Match match)
        {
            var org = match.Groups["org"].Value;
            var ds = match.Groups["ds"].Value;
            var tx = int.Parse(match.Groups["tx"].Value);
            var ty = int.Parse(match.Groups["ty"].Value);
            var tz = int.Parse(match.Groups["tz"].Value);
            var retina = int.Parse(match.Groups["retina"].Value) == 1;
            var path = match.Groups["path"].Value;

            var destPath = CommonUtils.SafeCombine(_settings.StaticFilesCachePath, tilesBase, org, ds,
                tz.ToString(), tx.ToString(), ty.ToString(), retina ? "1" : "0", path) + ".png";

            await SafeProcess(context, destPath,
                async () => await _objectsManager.GenerateTile(org, ds, path, tz, tx, ty, retina));
        }


        private async Task ProcessThumbnail(HttpContext context, Match match)
        {
            var org = match.Groups["org"].Value;
            var ds = match.Groups["ds"].Value;
            var size = int.Parse(match.Groups["size"].Value);
            var path = match.Groups["path"].Value;

            var destPath = CommonUtils.SafeCombine(_settings.StaticFilesCachePath, thumbsBase, org, ds,
                size.ToString(), path);

            await SafeProcess(context, destPath,
                async () => await _objectsManager.GenerateThumbnail(org, ds, path, size));
        }

        private async Task SafeProcess(HttpContext context, string destPath, Func<Task<FileDescriptorDto>> getData)
        {
            var folderName = Path.GetDirectoryName(destPath);
            Directory.CreateDirectory(folderName);
            var sigFile = destPath + ".sig";

            try
            {
                FileDescriptorDto descriptor;
                await using (var file = File.Open(sigFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                {
                    descriptor = await getData();

                    // Write content
                    await descriptor.ContentStream.CopyToAsync(file);
                }

                File.Move(sigFile, destPath);

                // Return it
                descriptor.ContentStream.Reset();
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = descriptor.ContentType;
                await descriptor.ContentStream.CopyToAsync(context.Response.Body);

                descriptor.ContentStream.Close();
            }
            catch (IOException ex)
            {
                try
                {
                    // Let's go safe: we wait until the generation is ok
                    await using var stream = await CommonUtils.WaitForFile(destPath, FileMode.Open, FileAccess.Read,
                        FileShare.None,
                        50, 1000);
                    if (stream == null)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        await context.Response.WriteAsJsonAsync(
                            new Registry.Web.Models.ErrorResponse(
                                "Cannot create thumbnail, process is taking too long"));
                        return;
                    }

                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "image/jpeg";
                    await stream.CopyToAsync(context.Response.Body);
                }
                catch (Exception e)
                {
                    await context.Response.ErrorResult(e);
                }
            }
            catch (ArgumentException ex)
            {
                await context.Response.Result(new Registry.Web.Models.ErrorResponse(ex.Message),
                    StatusCodes.Status404NotFound);
            }
            catch (UnauthorizedException ex)
            {
                await context.Response.Result(new Registry.Web.Models.ErrorResponse(ex.Message),
                    StatusCodes.Status401Unauthorized);
            }
            catch (Exception ex)
            {
                await context.Response.ErrorResult(ex);
            }
        }
    }
}