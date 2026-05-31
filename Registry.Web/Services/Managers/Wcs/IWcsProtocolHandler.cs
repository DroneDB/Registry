using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Services.Managers.Wcs;

/// <summary>
/// Per-version WCS protocol handler. One instance implements the wire format
/// (KVP parsing + XML serialization) for a single version (1.0.0 / 1.1.1 / 2.0.1).
/// Common logic (catalog resolution, raster rendering) is delegated to
/// <see cref="IWcsCoverageService"/> — handlers focus exclusively on the
/// version-specific protocol surface.
/// </summary>
public interface IWcsProtocolHandler
{
    /// <summary>WCS protocol version this handler implements (e.g. "1.0.0", "1.1.1", "2.0.1").</summary>
    string Version { get; }

    /// <summary>Render a GetCapabilities document for the dataset.</summary>
    Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath);

    /// <summary>Render a DescribeCoverage document. <paramref name="coverageIds"/> may contain
    /// a CSV value (1.0/2.0) or a single identifier (1.1). The handler parses as required.</summary>
    Task<string> DescribeCoverageAsync(string orgSlug, string dsSlug, string coverageIds);

    /// <summary>Parse KVP, render the coverage region, return bytes + content type.</summary>
    Task<WcsCoverageResult> GetCoverageAsync(string orgSlug, string dsSlug, IQueryCollection query);
}

/// <summary>Result of a GetCoverage operation: raw bytes + HTTP Content-Type.</summary>
public sealed record WcsCoverageResult(byte[] Bytes, string ContentType);
