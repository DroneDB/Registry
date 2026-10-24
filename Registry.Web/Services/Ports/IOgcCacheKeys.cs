#nullable enable
using System.Threading.Tasks;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Cache keys of a dataset's OGC documents (capabilities, layer lists). Every key carries the
/// dataset's current OGC generation, so invalidating them all is one write of a new generation:
/// no key scan, and it works on every cache provider (UPL-01 C.3).
/// </summary>
public interface IOgcCacheKeys
{
    /// <summary>The cache key of an OGC document of a dataset.</summary>
    /// <param name="orgSlug">Organization slug.</param>
    /// <param name="dsSlug">Dataset slug.</param>
    /// <param name="kind">What the document is, e.g. <c>caps-wms-v2-1.3.0</c> or <c>layers</c>.</param>
    /// <param name="detail">What else selects it (folder, sections), or null.</param>
    Task<string> ForAsync(string orgSlug, string dsSlug, string kind, string? detail = null,
        System.Threading.CancellationToken ct = default);

    /// <summary>Makes every key returned so far for the dataset unreachable.</summary>
    Task InvalidateAsync(string orgSlug, string dsSlug);
}
