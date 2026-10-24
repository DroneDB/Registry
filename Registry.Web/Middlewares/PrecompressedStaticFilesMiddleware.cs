#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Registry.Web.Middlewares;

/// <summary>
/// Serves the <c>.br</c> / <c>.gz</c> twin of a static file, compressed once at build time at the
/// highest level, instead of compressing it again on every request at a fast level (HUB-09).
/// It rewrites the request path to the twin and records the original so the static-file
/// middleware that follows can send the original content type; nothing else changes.
/// </summary>
public sealed class PrecompressedStaticFilesMiddleware(RequestDelegate next, Func<IFileProvider?> files)
{
    /// <summary>HttpContext item holding the original path of a request served from a twin.</summary>
    public const string OriginalPathItem = "precompressed-original-path";

    private static readonly (string Encoding, string Extension)[] Encodings = [("br", ".br"), ("gzip", ".gz")];
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
            request.Path.HasValue && System.IO.Path.HasExtension(request.Path.Value) &&
            files() is { } provider)
        {
            var accepted = request.Headers.AcceptEncoding.ToString();
            foreach (var (encoding, extension) in Encodings)
            {
                if (!Accepts(accepted, encoding)) continue;
                var twin = request.Path.Value + extension;
                if (!provider.GetFileInfo(twin).Exists) continue;

                context.Items[OriginalPathItem] = request.Path.Value;
                request.Path = twin;
                context.Response.Headers.ContentEncoding = encoding;
                context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
                break;
            }
        }

        return next(context);
    }

    /// <summary>The content type a precompressed response must carry (that of the original file), or null.</summary>
    public static string? OriginalContentType(HttpContext context) =>
        context.Items[OriginalPathItem] is string original && ContentTypes.TryGetContentType(original, out var type)
            ? type
            : null;

    // "br;q=0" refuses br explicitly
    private static bool Accepts(string acceptEncoding, string encoding) =>
        StringWithQualityHeaderValue.TryParseList(acceptEncoding.Split(','), out var values) &&
        values.Any(v => string.Equals(v.Value.Value, encoding, StringComparison.OrdinalIgnoreCase) &&
                        (v.Quality ?? 1) > 0);
}
