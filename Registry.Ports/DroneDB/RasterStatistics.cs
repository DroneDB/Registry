using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>What <c>DDBBuildRasterStatistics</c> found or did for the COG of a GeoRaster.</summary>
public enum RasterStatisticsStatus
{
    /// <summary>The statistics were computed and written next to the COG.</summary>
    Computed,

    /// <summary>The COG had them already.</summary>
    Present,

    /// <summary>The entry has no complete build: its build writes them.</summary>
    NotBuilt
}

/// <summary>The output of <c>DDBBuildRasterStatistics</c>: <c>{"status": "computed" | "present" | "notBuilt"}</c>.</summary>
public sealed class RasterStatisticsResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    /// <summary>The status as an enum; an unknown one is <see cref="RasterStatisticsStatus.NotBuilt"/> (nothing done).</summary>
    [JsonIgnore]
    public RasterStatisticsStatus Value => Status switch
    {
        "computed" => RasterStatisticsStatus.Computed,
        "present" => RasterStatisticsStatus.Present,
        _ => RasterStatisticsStatus.NotBuilt
    };
}
