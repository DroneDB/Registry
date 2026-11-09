namespace Registry.Web.Services.Objects;

/// <summary>
/// The contour lines asked for: an elevation step (<paramref name="Interval"/>, wins when both are set) or a
/// number of lines, an offset, an elevation range, a simplification tolerance and the band, and a NoData value that
/// replaces the one of the band (<paramref name="Nodata"/>, FR-RAS-31).
/// </summary>
public sealed record ContourOptions(
    double? Interval, int? Count, double BaseOffset, double? MinElev, double? MaxElev, double SimplifyTolerance,
    int BandIndex, double? Nodata = null);
