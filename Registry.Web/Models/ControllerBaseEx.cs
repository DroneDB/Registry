#nullable enable
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Registry.Web.Utilities;

namespace Registry.Web.Models;

/// <summary>
/// Base controller for Registry controllers. Since the exception-handling unification,
/// every controller exception is handled centrally by the global
/// <see cref="Registry.Web.Utilities.ApiExceptionFilter"/>, whose status/noRetry/message
/// rules live in <see cref="Registry.Web.Utilities.ApiExceptionClassifier"/>; per-action
/// error wrappers and the legacy in-controller result builders no longer exist.
/// </summary>
public class ControllerBaseEx : ControllerBase
{
    /// <summary>
    /// A file on disk with its validators (RQ-13): <c>Last-Modified</c> from the file, <c>ETag</c> when the caller knows
    /// one, and range processing, so the framework answers <c>If-None-Match</c> (304), <c>If-Modified-Since</c>,
    /// <c>Range</c> (206) and <c>If-Range</c> itself.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="contentType">Its media type.</param>
    /// <param name="entityTag">A quoted entity tag (<c>"…"</c> or <c>W/"…"</c>), or null for none.</param>
    /// <param name="downloadName">The download name (<c>Content-Disposition</c>), or null for none.</param>
    [NonAction]
    public PhysicalFileResult ValidatedPhysicalFile(string path, string contentType, string? entityTag,
        string? downloadName = null) =>
        PhysicalFile(path, contentType, downloadName, new FileInfo(path).LastWriteTimeUtc,
            entityTag is null ? null! : EntityTagHeaderValue.Parse(entityTag), enableRangeProcessing: true);

    /// <summary>
    /// 200 with <paramref name="value"/> for an action declared <c>ActionResult&lt;T&gt;</c> whose <c>T</c> is an interface
    /// (<c>IEnumerable&lt;X&gt;</c>): C# has no implicit conversion from an interface type to <c>ActionResult&lt;T&gt;</c>, so
    /// <c>return list;</c> does not compile there. Name <typeparamref name="T"/> when the value's static type is another one.
    /// </summary>
    [NonAction]
    public static ActionResult<T> Answer<T>(T value) => new(value);

    /// <summary>
    /// 200 with <paramref name="value"/>, or 304 when the request's <c>If-None-Match</c> has <paramref name="entityTag"/>;
    /// both carry the tag and <paramref name="cacheControl"/>. For a JSON response whose tag the caller computed.
    /// </summary>
    [NonAction]
    public IActionResult OkUnlessNotModified(object value, string entityTag, string cacheControl)
    {
        var tag = EntityTagHeaderValue.Parse(entityTag);
        var headers = Response.GetTypedHeaders();
        headers.ETag = tag;
        headers.CacheControl = CacheControlHeaderValue.Parse(cacheControl);
        return EntityTags.IfNoneMatch(Request, tag) ? StatusCode(StatusCodes.Status304NotModified) : Ok(value);
    }
}
