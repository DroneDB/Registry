using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The coordinate system of a build artifact as libddb names it (ddb.h "build manifest"). A point cloud's COPC
/// can be in another CRS than the uploaded file: libddb streams a cloud in longitude/latitude or Earth-centred
/// coordinates in UTM of its centre, and leaves the uploaded file unchanged.
/// </summary>
public sealed class ArtifactCrs
{
    /// <summary>The CRS name, e.g. "WGS 84 / UTM zone 33N".</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The EPSG code when the CRS has one, else null.</summary>
    [JsonPropertyName("epsg")]
    public int? Epsg { get; set; }

    /// <summary>True when the artifact's coordinates were transformed from the source file's CRS.</summary>
    [JsonPropertyName("transformed")]
    public bool Transformed { get; set; }
}
