using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Registry.Common;
using Registry.Common.Model;
using Registry.Ports.DroneDB;

namespace Registry.Web.Models.DTO;

public class EntryDto
{
    [JsonProperty("path")]
    public string Path { get; set; }

    [JsonProperty("hash")]
    public string Hash { get; set; }

    [JsonProperty("type")]
    public EntryType Type { get; set; }

    [JsonProperty("properties")]
    public Dictionary<string, object> Properties { get; set; }

    [JsonProperty("mtime")]
    [JsonConverter(typeof(SecondEpochConverter))]
    public DateTime ModifiedTime { get; set; }

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("depth")]
    public int Depth { get; set; }

    [JsonProperty("point_geom")]
    public object PointGeometry { get; set; }

    [JsonProperty("polygon_geom")]
    public object PolygonGeometry { get; set; }

    /// <summary>
    /// Build status of this entry: "queued" (not built yet, auto-build not
    /// started), "building" (a build job is active), "pending" (build
    /// deferred, see <see cref="BuildMissingDependencies"/>), or "failed"
    /// (last build attempt failed). Null when the entry is not buildable or
    /// its build is already complete ("ready" is the default state and is
    /// not transmitted).
    /// </summary>
    [JsonProperty("buildStatus", NullValueHandling = NullValueHandling.Ignore)]
    public string BuildStatus { get; set; }

    /// <summary>
    /// Names of the dependencies (companion/sidecar files or external tools)
    /// still missing, blocking the build. Only set when
    /// <see cref="BuildStatus"/> is "pending".
    /// </summary>
    [JsonProperty("buildMissingDependencies", NullValueHandling = NullValueHandling.Ignore)]
    public string[] BuildMissingDependencies { get; set; }
}