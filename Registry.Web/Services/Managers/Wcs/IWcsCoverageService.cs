using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO.Ogc;

namespace Registry.Web.Services.Managers.Wcs;

/// <summary>
/// Domain operations shared by every WCS protocol handler (1.0 / 1.1 / 2.0):
/// dataset / coverage resolution, raster probing, region rendering.
/// Centralizes all interactions with <see cref="IDDB"/>, the build artifact
/// resolver and <c>DdbWrapper.RenderRasterRegion</c> so handlers can stay
/// focused on the wire format.
/// </summary>
public interface IWcsCoverageService
{
    /// <summary>List all raster coverages exposed by the dataset (already filtered to
    /// <see cref="Registry.Ports.DroneDB.EntryType.GeoRaster"/> with a valid WGS84 bbox).</summary>
    Task<(IDDB Ddb, IReadOnlyList<OgcLayerDto> Layers)> GetCoveragesAsync(
        string orgSlug, string dsSlug, string? folderPath);

    /// <summary>Resolve a single coverage by id. Throws <see cref="Registry.Web.Exceptions.OgcException"/>
    /// with code <c>NoSuchCoverage</c> when the id is unknown.</summary>
    Task<(IDDB Ddb, OgcLayerDto Layer)> ResolveCoverageAsync(
        string orgSlug, string dsSlug, string coverageId);

    /// <summary>Resolve multiple coverages at once. Missing ids are reported in one go.</summary>
    Task<(IDDB Ddb, IReadOnlyList<(string Id, OgcLayerDto Layer)> Coverages)> ResolveCoveragesAsync(
        string orgSlug, string dsSlug, IEnumerable<string> coverageIds);

    /// <summary>Probe raster width/height/band metadata for a coverage. Failures are caught
    /// and reported as zeros so callers can fall back to defaults.</summary>
    WcsRasterInfo ProbeRaster(IDDB ddb, OgcLayerDto layer);

    /// <summary>Render the requested region of a coverage to the chosen MIME format.</summary>
    /// <param name="ddb">Resolved DDB handle for the dataset.</param>
    /// <param name="layer">Resolved coverage.</param>
    /// <param name="bboxWgs84">[minLon, minLat, maxLon, maxLat] in EPSG:4326.</param>
    /// <param name="width">Target pixel width. Pass 0 to auto-compute from bbox.</param>
    /// <param name="height">Target pixel height. Pass 0 to auto-compute from bbox.</param>
    /// <param name="mime">Output MIME type (e.g. image/tiff, image/png, image/jpeg).</param>
    byte[] RenderRegion(IDDB ddb, OgcLayerDto layer, double[] bboxWgs84, int width, int height, string mime);

    /// <summary>Absolute base URL of the WCS endpoint for the current request (no trailing '?').</summary>
    string GetBaseUrl(string orgSlug, string dsSlug, string? folderPath);
}

/// <summary>Probed raster metadata used by every WCS describe / grid synthesis.</summary>
public sealed record WcsRasterInfo(int Width, int Height, int BandCount, IReadOnlyList<string> BandNames)
{
    public static WcsRasterInfo Empty { get; } = new(0, 0, 0, []);
}
