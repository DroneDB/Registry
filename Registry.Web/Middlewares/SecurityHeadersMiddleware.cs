using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Registry.Web.Middlewares;

/// <summary>
/// Adds security-related HTTP headers to all responses. A page may be framed by another site only when it is a viewer
/// page opened with <c>?embed=1</c> (<see cref="IsEmbeddablePage"/>): every other page, and the API, always answers
/// <c>X-Frame-Options: SAMEORIGIN</c>, with or without that parameter, so a site cannot frame the sign-in, account,
/// upload, administration or report pages of a signed-in user.
/// </summary>
public sealed partial class SecurityHeadersMiddleware : IMiddleware
{
    // The viewer pages of a dataset (/r/{org}/{ds}/view/{encoded path}/{viewer}) and its saved views
    // (/r/{org}/{ds}/views/{id}), in the raw form they are requested in: the encoded path of a file may hold %2F, %2B or %3D,
    // which one segment of it still matches. Agreed with the Hub (its router lets the same routes be embedded)
    [GeneratedRegex(@"^/r/[^/]+/[^/]+/(view/[^/]+/(map|pointcloud|markdown|model|unified|splat|panorama)|views/[^/]+)/?$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex EmbeddablePagePattern();

    /// <summary>Whether <paramref name="request"/> is a viewer page opened with <c>?embed=1</c>.</summary>
    internal static bool IsEmbeddablePage(HttpRequest request) =>
        request.Query["embed"] == "1" && IsEmbeddablePath(RawPathOf(request));

    /// <summary>Whether <paramref name="rawPath"/> (percent-encoded, without the query) is a viewer or saved view page.</summary>
    internal static bool IsEmbeddablePath(string rawPath)
    {
        try
        {
            return EmbeddablePagePattern().IsMatch(rawPath);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // The path as it was requested, still percent-encoded: Request.Path is decoded, which would let the "/" of an
    // encoded segment (%2F) split it. A host that does not fill the raw target (a test server) gives the path re-encoded
    private static string RawPathOf(HttpRequest request)
    {
        var raw = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw))
            return request.Path.ToUriComponent();

        var end = raw.AsSpan().IndexOfAny('?', '#');
        return end < 0 ? raw : raw[..end];
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Decided before the response starts, from the request as it came in
        var embeddable = IsEmbeddablePage(context.Request);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";

            // Cross-origin embedding only for an embeddable page; everything else is restricted to the same origin
            if (embeddable)
                headers.ContentSecurityPolicy = "frame-ancestors *";
            else
                headers.XFrameOptions = "SAMEORIGIN";

            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            return Task.CompletedTask;
        });

        await next(context);
    }
}
