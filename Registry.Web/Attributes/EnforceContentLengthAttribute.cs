using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Attributes;

/// <summary>Which configured limit <see cref="EnforceContentLengthAttribute"/> applies.</summary>
public enum ContentLengthLimit
{
    /// <summary><see cref="HttpHostOptions.MaxRequestBodySize"/> (null: unlimited).</summary>
    RequestBody,

    /// <summary>One chunk of a resumable upload: <see cref="UploadSettings.MaxChunkBytes"/> plus
    /// <see cref="EnforceContentLengthFilter.ChunkFramingBytes"/> of multipart framing.</summary>
    Chunk,

    /// <summary>A number of bytes the action itself fixes (<see cref="EnforceContentLengthAttribute(long, string)"/>).</summary>
    Fixed
}

/// <summary>
/// The limit one action applies: <see cref="Limit"/>, and for <see cref="ContentLengthLimit.Fixed"/> the
/// <see cref="MaxBytes"/> allowed and the <see cref="Subject"/> the message names.
/// </summary>
public sealed record ContentLengthBound(ContentLengthLimit Limit, long MaxBytes = 0, string Subject = "");

/// <summary>
/// Rejects a request whose declared <c>Content-Length</c> is above the configured limit with
/// <see cref="PayloadTooLargeException"/> (413), before any byte of the body is read or stored. The one
/// place of that rule: it used to be copied into three upload actions. Kestrel still enforces
/// the limit mid-stream for a body without a length (<see cref="ConfigurableUploadSizeLimitAttribute"/>);
/// the classifier maps that 413 too, as it does for <c>[RequestSizeLimit]</c>, which an action with a fixed limit keeps
/// for that reason. A resource filter, so the exception reaches the pipeline's exception handler, which writes the
/// ProblemDetails body.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EnforceContentLengthAttribute : TypeFilterAttribute
{
    /// <summary>A configured limit; <see cref="ContentLengthLimit.Fixed"/> needs the byte count, so it takes the other
    /// constructor.</summary>
    public EnforceContentLengthAttribute(ContentLengthLimit limit = ContentLengthLimit.RequestBody)
        : base(typeof(EnforceContentLengthFilter))
    {
        if (limit == ContentLengthLimit.Fixed)
            throw new ArgumentException("A fixed limit needs its byte count and subject", nameof(limit));

        Arguments = [new ContentLengthBound(limit)];
    }

    /// <summary>At most <paramref name="maxBytes"/>; the 413 says "<paramref name="subject"/> can be at most N bytes".</summary>
    public EnforceContentLengthAttribute(long maxBytes, string subject)
        : base(typeof(EnforceContentLengthFilter))
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        Arguments = [new ContentLengthBound(ContentLengthLimit.Fixed, maxBytes, subject)];
    }
}

/// <summary>The filter of <see cref="EnforceContentLengthAttribute"/>.</summary>
public sealed class EnforceContentLengthFilter : IResourceFilter
{
    /// <summary>Multipart framing allowed around a chunk (boundaries, part headers, form fields).</summary>
    public const long ChunkFramingBytes = 64 * 1024;

    private readonly HttpHostOptions _http;
    private readonly UploadSettings _uploads;
    private readonly ContentLengthBound _bound;

    public EnforceContentLengthFilter(IOptions<HttpHostOptions> http, IOptions<UploadSettings> uploads, ContentLengthBound bound)
    {
        _http = http.Value;
        _uploads = uploads.Value;
        _bound = bound;
    }

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.HttpContext.Request.ContentLength is not { } length)
            return;

        if (_bound.Limit == ContentLengthLimit.Chunk)
        {
            var maxChunk = _uploads.MaxChunkBytes;
            if (length > maxChunk + ChunkFramingBytes)
                throw new PayloadTooLargeException($"A chunk can be at most {maxChunk} bytes");
        }
        else if (_bound.Limit == ContentLengthLimit.Fixed)
        {
            if (length > _bound.MaxBytes)
                throw new PayloadTooLargeException($"{_bound.Subject} can be at most {_bound.MaxBytes} bytes");
        }
        else if (_http.MaxRequestBodySize is { } maxBody && length > maxBody)
        {
            throw new PayloadTooLargeException($"Upload exceeds the maximum allowed size of {maxBody} bytes");
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
        // Nothing to do after the action
    }
}
