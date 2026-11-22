using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// An image whose footprint intersects a geometry (<c>DDBFindCoverage</c>): the answer of libddb, passed through.
/// </summary>
public class CoverageResult
{
    /// <summary>The entry path of the image.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Ascending is best: the distance from the centroid of the geometry to the centroid of the footprint over the
    /// diagonal of the footprint, so 0 is the image whose middle is the geometry.
    /// </summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>The share of a polygon the footprint covers (0 to 1); 1 for a point or a line that intersects it.</summary>
    [JsonPropertyName("overlap")]
    public double Overlap { get; set; }

    /// <summary>
    /// The pixel <c>[x, y]</c> of the centroid of the geometry in the image; null when the camera parameters do not
    /// allow the projection or the centroid is outside the image.
    /// </summary>
    [JsonPropertyName("pixel")]
    public double[]? Pixel { get; set; }
}
