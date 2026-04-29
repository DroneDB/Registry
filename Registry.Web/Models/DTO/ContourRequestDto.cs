#nullable enable
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Request body for <c>POST contours</c>. Carries the contour generation
/// parameters for a DEM/DSM/DTM raster.
/// </summary>
/// <remarks>
/// Either <see cref="Interval"/> or <see cref="Count"/> must be supplied.
/// When both are set, <see cref="Interval"/> wins.
/// </remarks>
public class ContourRequestDto
{
    /// <summary>Dataset-relative path of the raster.</summary>
    [Required]
    public string Path { get; set; } = null!;

    /// <summary>Vertical spacing between contour levels (raster units, &gt; 0).</summary>
    public double? Interval { get; set; }

    /// <summary>Target number of contour levels (&gt; 0). Used when <see cref="Interval"/> is null.</summary>
    public int? Count { get; set; }

    /// <summary>Reference base elevation (default 0).</summary>
    public double BaseOffset { get; set; }

    /// <summary>Drop contours below this elevation. Null disables the lower bound.</summary>
    public double? MinElev { get; set; }

    /// <summary>Drop contours above this elevation. Null disables the upper bound.</summary>
    public double? MaxElev { get; set; }

    /// <summary>Geometry simplification tolerance in raster CRS units (0 = none).</summary>
    public double SimplifyTolerance { get; set; }

    /// <summary>1-based raster band index (default 1).</summary>
    public int BandIndex { get; set; } = 1;
}
