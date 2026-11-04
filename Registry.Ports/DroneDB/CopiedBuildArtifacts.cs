using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>The result of <c>DDBCopyBuildArtifacts</c>: the destination's artifacts after the copy.</summary>
public sealed class CopiedBuildArtifacts
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    /// <summary>The artifacts of the destination after the copy (as in a build object).</summary>
    [JsonPropertyName("artifacts")]
    public IReadOnlyList<BuildArtifact> Artifacts { get; set; } = [];

    /// <summary>The destination has a pending marker (a deferred build stays deferred).</summary>
    [JsonPropertyName("pending")]
    public bool Pending { get; set; }
}
