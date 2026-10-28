#nullable enable
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Registry.Web.Utilities;

/// <summary>The one <c>If-None-Match</c> evaluation of the responses Registry validates itself.</summary>
public static class EntityTags
{
    /// <summary>
    /// The quoted entity tag of a file's content as the file system knows it: the time of its last write and its
    /// length, the validator of ASP.NET Core's static files. It changes when the file is rewritten, which a forced
    /// rebuild does under the same build hash.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="weak">
    /// True for <c>W/"…"</c>: the tag then vouches for the content, not for the bytes on the wire. A response that
    /// may be sent with a <c>Content-Encoding</c> must be tagged weak, since the gzip and the identity bytes are
    /// different representations (RFC 9110 §8.8.3) and a strong tag shared by both would let <c>If-Range</c> join
    /// a range of one to the bytes of the other.
    /// </param>
    public static string OfFile(FileInfo file, bool weak = false) =>
        $"{(weak ? "W/" : "")}\"{file.LastWriteTimeUtc.Ticks:x}-{file.Length:x}\"";

    /// <summary>
    /// True when the request's <c>If-None-Match</c> lists <paramref name="tag"/> (weak comparison, as RFC 9110
    /// §13.1.2 requires for If-None-Match) or is <c>*</c>: the client has the current representation.
    /// </summary>
    public static bool IfNoneMatch(HttpRequest request, EntityTagHeaderValue tag)
    {
        var header = request.Headers.IfNoneMatch;
        if (StringValues.IsNullOrEmpty(header) || !EntityTagHeaderValue.TryParseList(header, out var tags))
            return false;

        return tags.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(tag, useStrongComparison: false));
    }
}
