using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Ports;

namespace Registry.Web.Middlewares;

/// <summary>
/// Middleware that limits concurrent downloads per user (or per IP for anonymous users).
/// Intercepts download endpoints (/download, /ddb) and enforces the configured limit.
/// Admins are exempt from the limit.
///
/// Supports a preflight check via the X-Download-Check header:
/// when present, the middleware checks slot availability without acquiring,
/// returning 200 or 429 immediately. This allows the frontend to verify
/// before initiating a native browser download.
/// </summary>
public partial class DownloadLimitMiddleware : IMiddleware
{
    private readonly IDownloadLimiter _downloadLimiter;
    private readonly IAuthManager _authManager;
    private readonly ILogger<DownloadLimitMiddleware> _logger;

    private const string PreflightHeader = "X-Download-Check";
    private const string TooManyDownloadsMessage = "Too many concurrent downloads. Please wait for a download to finish.";

    // Matches paths like /orgs/{org}/ds/{ds}/download or /orgs/{org}/ds/{ds}/ddb
    [GeneratedRegex(@"/orgs/[^/]+/ds/[^/]+/(download|ddb)(/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DownloadPathRegex();

    public DownloadLimitMiddleware(
        IDownloadLimiter downloadLimiter,
            IAuthManager authManager,
        ILogger<DownloadLimitMiddleware> logger)
    {
        _downloadLimiter = downloadLimiter;
        _authManager = authManager;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Skip if the limiter is disabled
        if (!_downloadLimiter.IsEnabled)
        {
            await next(context);
            return;
        }

        // Only intercept download endpoints
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path) || !DownloadPathRegex().IsMatch(path))
        {
            await next(context);
            return;
        }

        // Admin bypass: admins are never limited
        if (await _authManager.IsUserAdmin())
        {
            await next(context);
            return;
        }

        // Determine the key: authenticated user ID or remote IP for anonymous
        var key = GetLimiterKey(context);

        // Preflight check: X-Download-Check header means "just check, don't acquire"
        if (context.Request.Headers.ContainsKey(PreflightHeader))
        {
            if (_downloadLimiter.CanAcquireSlot(key))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return;
            }

            _logger.LogDebug("Preflight download check failed for key '{Key}' on path '{Path}'", key, path);
            await WriteTooManyRequestsResponse(context);
            return;
        }

        // Actual download: try to acquire a slot
        if (!_downloadLimiter.TryAcquireSlot(key))
        {
            _logger.LogWarning("Download limit exceeded for key '{Key}' on path '{Path}'", key, path);
            await WriteTooManyRequestsResponse(context);
            return;
        }

        try
        {
            await next(context);
        }
        finally
        {
            // Always release the slot, even if the request was cancelled or threw an exception
            _downloadLimiter.ReleaseSlot(key);
        }
    }

    /// <summary>
    /// Writes a 429 Too Many Requests response.
    /// Returns a user-friendly HTML page for direct browser navigation (Accept: text/html),
    /// or JSON for API/fetch requests.
    /// </summary>
    private static async Task WriteTooManyRequestsResponse(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["Retry-After"] = "10";

        var accept = context.Request.Headers.Accept.ToString();

        // Browser direct navigation sends Accept: text/html
        // fetch() sends Accept: */* by default (no text/html preference)
        if (accept.Contains("text/html") && !context.Request.Headers.ContainsKey(PreflightHeader))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync($$"""
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset="utf-8">
                    <title>Download Limit Reached</title>
                    <style>
                        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                               display: flex; justify-content: center; align-items: center; min-height: 100vh;
                               margin: 0; background: #f5f5f5; color: #333; }
                        .card { background: white; border-radius: 8px; padding: 40px; max-width: 500px;
                                 box-shadow: 0 2px 10px rgba(0,0,0,0.1); text-align: center; }
                        h1 { color: #e74c3c; font-size: 1.5em; margin-bottom: 16px; }
                        p { color: #666; line-height: 1.6; }
                        button { margin-top: 20px; padding: 10px 24px; background: #3498db; color: white;
                                  border: none; border-radius: 4px; font-size: 1em; cursor: pointer; }
                        button:hover { background: #2980b9; }
                    </style>
                </head>
                <body>
                    <div class="card">
                        <h1>&#9888; Download Limit Reached</h1>
                        <p>{{TooManyDownloadsMessage}}</p>
                        <button onclick="history.back()">Go Back</button>
                    </div>
                </body>
                </html>
                """);
        }
        else
        {
            context.Response.ContentType = "application/json";
            var body = new { error = TooManyDownloadsMessage };
            await context.Response.WriteAsync(JsonSerializer.Serialize(body));
        }
    }

    /// <summary>
    /// Gets the limiter key for the current request.
    /// Uses the authenticated user ID if available, otherwise falls back to the remote IP address.
    /// Considers X-Forwarded-For header for reverse proxy scenarios.
    /// </summary>
    private static string GetLimiterKey(HttpContext context)
    {
        // Try authenticated user ID first
        var userId = context.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;
        if (!string.IsNullOrEmpty(userId))
            return $"user:{userId}";

        // Fall back to IP address (consider X-Forwarded-For for reverse proxy)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            // Take the first IP in the chain (original client)
            var clientIp = forwardedFor.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(clientIp))
                return $"ip:{clientIp}";
        }

        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{remoteIp}";
    }
}
