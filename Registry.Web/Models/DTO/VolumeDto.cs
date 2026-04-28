#nullable enable
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Request body for <c>POST stockpile/calculate</c>. The polygon is sent in the body because
/// GeoJSON geometries can exceed the URL length limit.
/// </summary>
public class VolumeCalculationRequestDto
{
    /// <summary>Dataset-relative path of the elevation raster.</summary>
    [Required]
    public string Path { get; set; } = null!;

    /// <summary>GeoJSON Polygon or MultiPolygon geometry in WGS84 (EPSG:4326).</summary>
    [Required]
    public string Polygon { get; set; } = null!;

    /// <summary>
    /// Base plane method. One of: <c>lowest_perimeter</c> (default),
    /// <c>average_perimeter</c>, <c>best_fit</c>, <c>flat</c>.
    /// </summary>
    public string? BaseMethod { get; set; }

    /// <summary>Elevation used when <see cref="BaseMethod"/> == <c>flat</c>.</summary>
    public double FlatElevation { get; set; }

    /// <summary>Optional material slug to include weight/cost estimates in the response.</summary>
    public string? Material { get; set; }
}

/// <summary>Request body for <c>POST stockpile/detect</c>.</summary>
public class StockpileDetectionRequestDto
{
    /// <summary>Dataset-relative path of the elevation raster.</summary>
    [Required]
    public string Path { get; set; } = null!;

    /// <summary>Click latitude (WGS84).</summary>
    [Required]
    public double Lat { get; set; }

    /// <summary>Click longitude (WGS84).</summary>
    [Required]
    public double Lon { get; set; }

    /// <summary>Search radius in meters (defaults to 50 m).</summary>
    public double? Radius { get; set; }

    /// <summary>Sensitivity in [0, 1]. Higher = more detail. Defaults to 0.5.</summary>
    public float? Sensitivity { get; set; }
}

/// <summary>Request body for <c>POST stockpile/detect-all</c>.</summary>
public class StockpileBatchDetectionRequestDto
{
    /// <summary>Dataset-relative path of the elevation raster.</summary>
    [Required]
    public string Path { get; set; } = null!;

    /// <summary>Sensitivity in [0, 1]. Higher = more detail. Defaults to 0.5.</summary>
    public float? Sensitivity { get; set; }

    /// <summary>Minimum stockpile area in square meters. Defaults to 5.</summary>
    public double? MinAreaM2 { get; set; }

    /// <summary>Maximum number of stockpiles to return. Defaults to 50, server-capped at 500.</summary>
    public int? MaxResults { get; set; }
}

/// <summary>Static material info used for weight/cost estimation.</summary>
public class MaterialInfoDto
{
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Category { get; set; } = null!;
    /// <summary>Bulk density in t/m³.</summary>
    public double DensityTonPerM3 { get; set; }
    /// <summary>Reference cost per ton (same currency across the list).</summary>
    public double CostPerTon { get; set; }
    public string Currency { get; set; } = "USD";
}
