#nullable enable
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Request body for <c>POST raster-profile</c>. Carries the polyline via body
/// because GeoJSON geometries can exceed query-string length limits.
/// </summary>
public class RasterProfileRequestDto
{
    /// <summary>Dataset-relative path of the raster.</summary>
    [Required]
    public string Path { get; set; } = null!;

    /// <summary>GeoJSON LineString geometry expressed in WGS84 (EPSG:4326).</summary>
    [Required]
    public string LineString { get; set; } = null!;

    /// <summary>Requested number of equispaced samples (optional, server clamps 2..4096).</summary>
    public int? Samples { get; set; }
}
