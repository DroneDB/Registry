using System.Text.Json;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Request body for <c>POST coverage</c>: the place whose photos are wanted.
/// </summary>
public class CoverageRequestDto
{
    /// <summary>
    /// A GeoJSON geometry object in WGS84 (EPSG:4326): a Point, a LineString or a Polygon of at most 10000 positions.
    /// </summary>
    public JsonElement Geometry { get; set; }

    /// <summary>The most images returned, 1 to 100 (default 20).</summary>
    public int? Limit { get; set; }
}
