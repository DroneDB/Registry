namespace Registry.Ports.DroneDB;

/// <summary>
/// The options every raster value call shares (libddb's <c>DDBRasterOptions</c>): value info, point, area, profile and
/// contours take this one type, never a parameter per endpoint (FR-RAS-31, FR-XC-24, D-022).
/// </summary>
/// <param name="Nodata">A NoData value that replaces the one of the band (finite); null keeps the band's own.</param>
/// <param name="Formula">The formula the values are read through: for the value info of a vegetation index, the index (its
/// default stretch is that of the index, D-041); a thermal formula names the unit of the values (D-022).</param>
/// <param name="Bbox">Value info only: a WGS 84 window <c>[minLon, minLat, maxLon, maxLat]</c>; the info reports the
/// source pixels in it.</param>
public sealed record RasterAnalysisOptions(double? Nodata = null, string? Formula = null, double[]? Bbox = null)
{
    /// <summary>No option set.</summary>
    public static readonly RasterAnalysisOptions None = new();

    /// <summary>True when nothing is set (the call can use the export without options).</summary>
    public bool IsEmpty => Nodata is null && string.IsNullOrWhiteSpace(Formula) && Bbox is null;
}
