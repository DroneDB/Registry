#nullable enable
using System;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// OGC API – Tiles tile matrix sets: the one set the tile sets of a collection advertise and the tile requests
/// accept (OGC 20-057, requirement tile-matrix-set: an unknown <c>tileMatrixSetId</c> is a 404).
/// </summary>
public static class OgcApiTilesConformance
{
    /// <summary>The identifier of the only tile matrix set (Web Mercator, 256 px tiles, XYZ).</summary>
    public const string WebMercatorQuad = "WebMercatorQuad";

    /// <summary>The OGC definition URI of <see cref="WebMercatorQuad"/>.</summary>
    public const string WebMercatorQuadUri = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad";

    /// <summary>The tile matrix set identifiers are case-sensitive.</summary>
    public static bool IsSupported(string? tileMatrixSetId) =>
        string.Equals(tileMatrixSetId, WebMercatorQuad, StringComparison.Ordinal);

    /// <exception cref="OgcException">A 404 <c>NotFound</c> for a tile matrix set the service does not publish.</exception>
    public static void Validate(string? tileMatrixSetId)
    {
        if (!IsSupported(tileMatrixSetId))
            throw new OgcException("NotFound",
                $"The tile matrix set '{tileMatrixSetId}' is not supported; only '{WebMercatorQuad}' is", 404,
                "tileMatrixSetId");
    }
}
