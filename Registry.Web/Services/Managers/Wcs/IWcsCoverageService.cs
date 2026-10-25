using System.Collections.Generic;

namespace Registry.Web.Services.Managers.Wcs;

/// <summary>Probed raster metadata used by every WCS describe / grid synthesis.</summary>
public sealed record WcsRasterInfo(int Width, int Height, int BandCount, IReadOnlyList<string> BandNames, string NativeCrs = "")
{
    public static WcsRasterInfo Empty { get; } = new(0, 0, 0, [], "");
}
