using System.Globalization;
using System.Text.Json;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// The tiles of a vector's MVT pyramid: <c>{z}/{x}/{y}.pbf</c> inside its <c>vector.mvt</c> directory artifact, the
/// layout GDAL's MVT driver writes and the artifact's descriptor describes. The one place that names a tile file.
/// </summary>
public static class MvtTiles
{
    /// <summary>Tile <paramref name="z"/>/<paramref name="x"/>/<paramref name="y"/>, relative to the pyramid.</summary>
    public static string Tile(int z, int x, int y) => string.Create(CultureInfo.InvariantCulture, $"{z}/{x}/{y}.pbf");

    /// <summary>
    /// Whether tile <paramref name="z"/>/<paramref name="x"/>/<paramref name="y"/> belongs to the pyramid that
    /// <paramref name="descriptor"/> (the text of its <c>metadata.json</c>) describes: a zoom level between its
    /// <c>minzoom</c> and <c>maxzoom</c>, and a column and row on the grid of that level. GDAL writes no file for a
    /// tile of the pyramid without features, so such a tile is empty, not missing. A descriptor that is missing or
    /// cannot be read covers nothing.
    /// </summary>
    public static bool Covers(string? descriptor, int z, int x, int y)
    {
        if (descriptor == null || z < 0 || z > 30 || x < 0 || y < 0 || x >= 1 << z || y >= 1 << z) return false;
        try
        {
            using var json = JsonDocument.Parse(descriptor);
            return ZoomOf(json.RootElement, "minzoom") is { } min && ZoomOf(json.RootElement, "maxzoom") is { } max
                   && z >= min && z <= max;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // A zoom level of the descriptor: a number, or a string of one (the MBTiles metadata writes strings)
    private static int? ZoomOf(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }
}
