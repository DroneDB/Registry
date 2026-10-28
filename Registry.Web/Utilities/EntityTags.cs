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
    /// The strong, quoted entity tag of a file's content as the file system knows it: the time of its last write and
    /// its length, the validator of ASP.NET Core's static files. It changes when the file is rewritten, which a
    /// forced rebuild does under the same build hash.
    /// </summary>
    public static string OfFile(FileInfo file) => $"\"{file.LastWriteTimeUtc.Ticks:x}-{file.Length:x}\"";

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
