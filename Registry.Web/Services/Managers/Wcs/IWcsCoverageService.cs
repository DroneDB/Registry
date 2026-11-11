using System;
using System.Collections.Generic;

namespace Registry.Web.Services.Managers.Wcs;

/// <summary>Probed raster metadata used by every WCS describe / grid synthesis.</summary>
public sealed record WcsRasterInfo(int Width, int Height, int BandCount, IReadOnlyList<string> BandNames, string NativeCrs = "")
{
    public static WcsRasterInfo Empty { get; } = new(0, 0, 0, [], "");
}


/// <summary>
/// The native pixel grid of a coverage in WGS 84 degrees: its size and the size of one pixel. DescribeCoverage of WCS 1.0,
/// 1.1 and 2.0 writes it and GetCoverage derives the size of a subset from it (FR-SHR-02, FR-XC-31).
/// </summary>
public readonly record struct WcsGrid(int Width, int Height, double Dx, double Dy)
{
    /// <summary>
    /// The size, in pixels, of the part of the grid that <paramref name="bbox"/> (<c>[minLon, minLat, maxLon, maxLat]</c>)
    /// covers: the span over the pixel size, rounded, at least one pixel on each axis.
    /// </summary>
    public (int Width, int Height) SizeOf(double[] bbox) =>
        (Math.Max(1, (int)Math.Round((bbox[2] - bbox[0]) / Dx)),
         Math.Max(1, (int)Math.Round((bbox[3] - bbox[1]) / Dy)));
}
