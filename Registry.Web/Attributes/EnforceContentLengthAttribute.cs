#nullable enable
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
    /// <summary><see cref="AppSettings.MaxRequestBodySize"/> (null: unlimited).</summary>
    RequestBody,

    /// <summary>One chunk of a resumable upload: <see cref="UploadSettings.MaxChunkBytes"/> plus
    /// <see cref="EnforceContentLengthFilter.ChunkFramingBytes"/> of multipart framing.</summary>
    Chunk
}

/// <summary>
/// Rejects a request whose declared <c>Content-Length</c> is above the configured limit with
/// <see cref="PayloadTooLargeException"/> (413), before any byte of the body is read or stored. The one
/// place of that rule (RQ-05): it used to be copied into three upload actions. Kestrel still enforces
/// the limit mid-stream for a body without a length (<see cref="ConfigurableUploadSizeLimitAttribute"/>);
/// the classifier maps that 413 too. A resource filter, so the exception reaches the pipeline's
/// exception handler, which writes the ProblemDetails body.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EnforceContentLengthAttribute : TypeFilterAttribute
{
    public EnforceContentLengthAttribute(ContentLengthLimit limit = ContentLengthLimit.RequestBody)
        : base(typeof(EnforceContentLengthFilter))
    {
        Arguments = [limit];
    }
}

/// <summary>The filter of <see cref="EnforceContentLengthAttribute"/>.</summary>
public sealed class EnforceContentLengthFilter : IResourceFilter
{
    /// <summary>Multipart framing allowed around a chunk (boundaries, part headers, form fields).</summary>
    public const long ChunkFramingBytes = 64 * 1024;

    private readonly AppSettings _settings;
    private readonly ContentLengthLimit _limit;

    public EnforceContentLengthFilter(IOptions<AppSettings> settings, ContentLengthLimit limit)
    {
        _settings = settings.Value;
        _limit = limit;
    }

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.HttpContext.Request.ContentLength is not { } length)
            return;

        if (_limit == ContentLengthLimit.Chunk)
        {
            var maxChunk = (_settings.Uploads ?? new UploadSettings()).MaxChunkBytes;
            if (length > maxChunk + ChunkFramingBytes)
                throw new PayloadTooLargeException($"A chunk can be at most {maxChunk} bytes");
        }
        else if (_settings.MaxRequestBodySize is { } maxBody && length > maxBody)
        {
            throw new PayloadTooLargeException($"Upload exceeds the maximum allowed size of {maxBody} bytes");
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
        // Nothing to do after the action
    }
}
