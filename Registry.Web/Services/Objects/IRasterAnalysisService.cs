#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The contour lines asked for: an elevation step (<paramref name="Interval"/>, wins when both are set) or a
/// number of lines, an offset, an elevation range, a simplification tolerance and the band.
/// </summary>
public sealed record ContourOptions(
    double? Interval, int? Count, double BaseOffset, double? MinElev, double? MaxElev, double SimplifyTolerance,
    int BandIndex);
