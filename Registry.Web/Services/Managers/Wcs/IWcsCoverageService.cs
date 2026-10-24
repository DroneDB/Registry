using System.Collections.Generic;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO.Ogc;

namespace Registry.Web.Services.Managers.Wcs;

/// <summary>Probed raster metadata used by every WCS describe / grid synthesis.</summary>
public sealed record WcsRasterInfo(int Width, int Height, int BandCount, IReadOnlyList<string> BandNames, string NativeCrs = "")
{
    public static WcsRasterInfo Empty { get; } = new(0, 0, 0, [], "");
}
