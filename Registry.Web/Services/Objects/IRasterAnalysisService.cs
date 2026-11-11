using Registry.Web.Exceptions;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The contour lines asked for: an elevation step (<paramref name="Interval"/>, wins when both are set) or a
/// number of lines, an offset, an elevation range, a simplification tolerance and the band, a NoData value that
/// replaces the one of the band (<paramref name="Nodata"/>, FR-RAS-31) and the formula the values are read through
/// (<paramref name="Formula"/>: a thermal formula gives the levels in its unit, D-022; libddb ignores an index formula).
/// </summary>
public sealed record ContourOptions(
    double? Interval, int? Count, double BaseOffset, double? MinElev, double? MaxElev, double SimplifyTolerance,
    int BandIndex, double? Nodata = null, string? Formula = null)
{
    /// <summary>
    /// The rules of a contour request, the same for the endpoint and for the <c>raster-contours</c> task (a refusal at the
    /// submit, not a failed task).
    /// </summary>
    /// <exception cref="BadRequestException">A rule is broken.</exception>
    public void Require()
    {
        if (!Interval.HasValue && !Count.HasValue)
            throw new BadRequestException("Either 'interval' or 'count' must be specified");
        if (Interval <= 0)
            throw new BadRequestException("'interval' must be > 0");
        if (Count <= 0)
            throw new BadRequestException("'count' must be > 0");
        if (MinElev >= MaxElev)
            throw new BadRequestException("'minElev' must be less than 'maxElev'");
        if (SimplifyTolerance < 0)
            throw new BadRequestException("'simplifyTolerance' must be >= 0");

        RasterOptionRules.RequireNodata(Nodata);
    }

    /// <summary>The options of the values libddb reads the contours with.</summary>
    public Registry.Ports.DroneDB.RasterAnalysisOptions? ValueOptions() =>
        Nodata is null && string.IsNullOrWhiteSpace(Formula) ? null : RasterOptionRules.Options(Nodata, Formula);
}
