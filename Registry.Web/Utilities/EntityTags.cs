#nullable enable
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Registry.Web.Utilities;

/// <summary>The one <c>If-None-Match</c> evaluation of the responses Registry validates itself (RQ-13).</summary>
public static class EntityTags
{
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
