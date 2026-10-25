using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Registry.Ports.Json;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Web.Models.DTO;

public class EntryDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; }

    [JsonPropertyName("hash")]
    public string Hash { get; set; }

    [JsonPropertyName("type")]
    public EntryType Type { get; set; }

    [JsonPropertyName("properties")]
    [JsonConverter(typeof(SharedDocumentMapConverter))]
    public Dictionary<string, JsonElement> Properties { get; set; }

    [JsonPropertyName("mtime")]
    [JsonConverter(typeof(SecondEpochConverter))]
    public DateTime ModifiedTime { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("point_geom")]
    public JsonElement? PointGeometry { get; set; }

    [JsonPropertyName("polygon_geom")]
    public JsonElement? PolygonGeometry { get; set; }

    /// <summary>
    /// Build status of this entry: "queued" (not built yet, auto-build not
    /// started), "building" (a build job is active), "pending" (build
    /// deferred, see <see cref="BuildMissingDependencies"/>), or "failed"
    /// (last build attempt failed). Null when the entry is not buildable or
    /// its build is already complete ("ready" is the default state and is
    /// not transmitted).
    /// </summary>
    [JsonPropertyName("buildStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string BuildStatus { get; set; }

    /// <summary>
    /// Names of the dependencies (companion/sidecar files or external tools)
    /// still missing, blocking the build. Only set when
    /// <see cref="BuildStatus"/> is "pending".
    /// </summary>
    [JsonPropertyName("buildMissingDependencies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[] BuildMissingDependencies { get; set; }

    /// <summary>
    /// The artifacts of the entry's own build that exist (libddb's build manifest), absent when there are
    /// none: a client reads them instead of composing build paths or probing files.
    /// </summary>
    [JsonPropertyName("artifacts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArtifactLinkDto[] Artifacts { get; set; }
}