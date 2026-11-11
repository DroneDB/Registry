using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>The result of <c>DDBCopyBuildArtifacts</c>: the destination's artifacts after the copy.</summary>
public sealed class CopiedBuildArtifacts
{
    /// <summary>The content hash of the entries whose build was copied.</summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    /// <summary>The build key that was copied (<see cref="EntryBuild.Key"/>); absent from a libddb that predates it.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>The artifacts of the destination after the copy (as in a build object).</summary>
    [JsonPropertyName("artifacts")]
    public IReadOnlyList<BuildArtifact> Artifacts { get; set; } = [];

    /// <summary>The destination has a pending marker (a deferred build stays deferred).</summary>
    [JsonPropertyName("pending")]
    public bool Pending { get; set; }
}
