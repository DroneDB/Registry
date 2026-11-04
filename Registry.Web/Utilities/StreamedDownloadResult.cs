using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Registry.Web.Utilities;

/// <summary>
/// A download written straight to the response while it is produced: no Content-Length (chunked), no
/// buffering on the way out, <c>Cache-Control: no-store</c>, and the work stops when the client goes away (that
/// is logged, not an error). Offered as an attachment named by <see cref="ContentDispositionHeader"/>, or shown
/// inline.
/// </summary>
public sealed class StreamedDownloadResult : IActionResult
{
    private readonly IStreamedDownload _download;
    private readonly bool _inline;
    private readonly ILogger _logger;

    public StreamedDownloadResult(IStreamedDownload download, bool inline, ILogger logger)
    {
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _inline = inline;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.HttpContext;
        var response = http.Response;

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = _download.ContentType;
        response.Headers.ContentDisposition = _inline ? "inline" : ContentDispositionHeader.Attachment(_download.Name);
        response.Headers.CacheControl = "no-store";
        response.Headers.ContentSecurityPolicy = UserContentPolicy.ContentSecurityPolicyFor(_download.ContentType);
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            await _download.CopyToAsync(response.Body, http.RequestAborted);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected: not an error
            _logger.LogDebug("Download of '{Name}' cancelled by the client ({Path})", _download.Name, http.Request.Path);
        }
    }
}

/// <summary>
/// The one writer of a download's <c>Content-Disposition</c> (RFC 6266): the quoted <c>filename</c> every client
/// reads (ASCII; other characters as '_', '"' and '\' escaped) and <c>filename*</c> with the exact UTF-8 name
/// (RFC 8187), which clients that know it prefer.
/// </summary>
public static class ContentDispositionHeader
{
    /// <summary>The header value offering <paramref name="fileName"/> as an attachment.</summary>
    public static string Attachment(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return $"attachment; filename=\"{AsciiQuoted(fileName)}\"; filename*=UTF-8''{Rfc8187(fileName)}";
    }

    // The content of a quoted-string: printable ASCII, with '"' and '\' escaped
    private static string AsciiQuoted(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is < ' ' or > '~')
                builder.Append('_');
            else
            {
                if (c is '"' or '\\')
                    builder.Append('\\');
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    // value-chars of RFC 8187: attr-char kept, every other UTF-8 byte percent-encoded
    private static string Rfc8187(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '!' or '#' or '$' or '&' or '+' or '-' or '.' or '^' or '_' or '`'
                    or '|' or '~')
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
