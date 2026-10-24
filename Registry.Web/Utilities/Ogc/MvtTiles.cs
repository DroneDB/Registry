#nullable enable
using System.Globalization;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// The tiles of a vector's MVT pyramid: <c>{z}/{x}/{y}.pbf</c> inside its <c>vector.mvt</c> directory artifact, the
/// layout GDAL's MVT driver writes and the artifact's descriptor describes. The one place that names a tile file.
/// </summary>
public static class MvtTiles
{
    /// <summary>Tile <paramref name="z"/>/<paramref name="x"/>/<paramref name="y"/>, relative to the pyramid.</summary>
    public static string Tile(int z, int x, int y) => string.Create(CultureInfo.InvariantCulture, $"{z}/{x}/{y}.pbf");
}
