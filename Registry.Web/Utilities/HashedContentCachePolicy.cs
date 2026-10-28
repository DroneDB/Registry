#nullable enable
using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Registry.Web.Utilities;

/// <summary>
/// HTTP caching policy for responses derived from one dataset entry (tiles, thumbnails).
/// The ETag is the entry hash plus the response variant (coordinates, size... and the write
/// time of the rendered artifact) plus the application version, so a changed entry, a rebuilt
/// artifact or a new renderer yields a new validator.
/// <para>
/// Responses are <c>private</c>: they sit behind dataset authorization and must never be
/// stored by shared caches. When the URL carries the entry hash (<c>v=&lt;hash&gt;</c>) and it
/// matches the current one, the URL identifies the content and the browser may reuse it
/// without revalidating for <see cref="VersionedMaxAge"/>. Otherwise the content behind the
/// URL can change at any time (the file at the path can be replaced), so the browser must
/// revalidate on every use and gets a 304 while the entry is unchanged.
/// </para>
/// <para>
/// <see cref="CacheControlFor"/> is that freshness rule on its own, for a response whose validator the caller
/// has already (the files of a build, whose URL names the version of their artifact).
/// </para>
/// </summary>
public sealed class HashedContentCachePolicy
{
    /// <summary>Bounded so that a renderer change in a new release reaches browsers within a day.</summary>
    public static readonly TimeSpan VersionedMaxAge = TimeSpan.FromDays(1);

    private static readonly string AppVersion =
        typeof(HashedContentCachePolicy).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0";

    private readonly EntityTagHeaderValue _etag;

    public string CacheControl { get; }

    private HashedContentCachePolicy(EntityTagHeaderValue etag, string cacheControl)
    {
        _etag = etag;
        CacheControl = cacheControl;
    }

    /// <param name="hash">Hash of the entry the response is derived from.</param>
    /// <param name="requestedVersion">The <c>v</c> query parameter, if the client sent one.</param>
    /// <param name="variant">What distinguishes responses for the same entry (e.g. z, x, y, retina).</param>
    public static HashedContentCachePolicy For(string hash, string? requestedVersion, params object[] variant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        var opaque = string.Join("-", new object[] { hash }.Concat(variant).Append(AppVersion)
            .Select(p => Sanitize(Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture))));

        return new HashedContentCachePolicy(new EntityTagHeaderValue($"\"{opaque}\""),
            CacheControlFor(hash, requestedVersion));
    }

    /// <summary>
    /// The <c>Cache-Control</c> of a response whose URL may name the version of its content: when
    /// <paramref name="requestedVersion"/> is <paramref name="currentVersion"/>, the URL identifies the bytes and the
    /// browser may reuse them without asking for <see cref="VersionedMaxAge"/> (<c>immutable</c>); otherwise (no
    /// version, a stale one, or no current version to compare with) it must revalidate on every use.
    /// </summary>
    /// <param name="currentVersion">What identifies the content now (an entry hash, the version of a build artifact).</param>
    /// <param name="requestedVersion">The version the request's URL names, if any.</param>
    public static string CacheControlFor(string? currentVersion, string? requestedVersion) =>
        !string.IsNullOrEmpty(currentVersion) &&
        string.Equals(requestedVersion, currentVersion, StringComparison.OrdinalIgnoreCase)
            ? $"private, max-age={(int)VersionedMaxAge.TotalSeconds}, immutable"
            : "private, no-cache";

    /// <summary>
    /// True when the request's <c>If-None-Match</c> lists this ETag (weak comparison, as
    /// RFC 9110 §13.1.2 requires for If-None-Match) or is <c>*</c>.
    /// </summary>
    public bool IsNotModified(HttpRequest request) => EntityTags.IfNoneMatch(request, _etag);

    /// <summary>Sets the validator and freshness headers (on 200 and 304 responses alike).</summary>
    public void Apply(HttpResponse response)
    {
        response.Headers.ETag = _etag.ToString();
        response.Headers.CacheControl = CacheControl;
    }

    // ETag opaque-tags cannot contain '"' (RFC 9110 §8.8.3); version strings may contain '+'.
    private static string Sanitize(string? part) =>
        string.IsNullOrEmpty(part) ? "null" : part.Replace("\"", string.Empty).Replace(' ', '_');
}
