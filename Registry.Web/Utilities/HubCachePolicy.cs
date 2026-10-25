#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;
using Registry.Web.Middlewares;

namespace Registry.Web.Utilities;

/// <summary>
/// <c>Cache-Control</c> of the Hub's static files. A file whose name carries its content
/// hash never changes, so browsers keep it without asking again; <c>index.html</c> is always
/// revalidated so a Hub upgrade is picked up; other public files (Potree, images) are reused for
/// an hour and then revalidated, unless their URL carries the Hub version (<c>?v=</c>, the Potree
/// files the viewer loads), which makes them immutable too: a new release asks for new URLs.
/// </summary>
public static partial class HubCachePolicy
{
    public const string Immutable = "public, max-age=31536000, immutable";
    public const string Document = "no-cache, no-store, must-revalidate";
    public const string Public = "public, max-age=3600";

    // webpack: "[name].[contenthash:8].js|css" for chunks, "[hash][ext]" (20 hex) for asset modules
    [GeneratedRegex(@"^(?:.+\.[0-9a-f]{8}\.(?:js|css)|[0-9a-f]{20}\.(?:woff2?|ttf|eot|svg|png|jpe?g|gif|webp|wasm))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex HashedName();

    /// <summary>The policy of a file of the Hub, by its file name (or path).</summary>
    /// <param name="fileNameOrPath">The file served.</param>
    /// <param name="versioned">Whether the URL names the Hub version (<c>?v=</c>).</param>
    public static string For(string fileNameOrPath, bool versioned = false)
    {
        var name = Path.GetFileName(fileNameOrPath);
        if (string.Equals(name, "index.html", StringComparison.OrdinalIgnoreCase))
            return Document;
        return versioned || HashedName().IsMatch(name) ? Immutable : Public;
    }

    /// <summary>
    /// Options for serving the Hub's files with their policy: the static files of the build and the
    /// SPA default page (client routes such as <c>/login</c> answer with <c>index.html</c>, which must
    /// carry the same headers as <c>/index.html</c>). A build-time .br/.gz twin keeps the policy and
    /// the content type of the file it stands for.
    /// </summary>
    public static StaticFileOptions StaticFileOptions() => new()
    {
        ServeUnknownFileTypes = true,
        OnPrepareResponse = Apply
    };

    /// <summary>Sets the cache headers (and the content type of a precompressed twin) of one response.</summary>
    public static void Apply(StaticFileResponseContext ctx)
    {
        var headers = ctx.Context.Response.Headers;
        var original = ctx.Context.Items[PrecompressedStaticFilesMiddleware.OriginalPathItem] as string;

        var policy = For(original ?? ctx.File.Name, ctx.Context.Request.Query.ContainsKey("v"));
        headers.CacheControl = policy;
        if (policy == Document)
        {
            headers.Pragma = "no-cache";
            headers.Expires = "0";
        }

        if (PrecompressedStaticFilesMiddleware.OriginalContentType(ctx.Context) is { } contentType)
            headers.ContentType = contentType;
    }
}
