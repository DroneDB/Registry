#nullable enable
using System;
using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Information about a single build deferred because one or more
/// dependencies (companion files or external tools) were missing at the
/// time of the last attempt.
/// </summary>
public class PendingBuildInfoDto
{
    /// <summary>Dataset-relative path of the entry whose build is pending.</summary>
    public string Path { get; set; } = null!;

    /// <summary>Content hash of the entry.</summary>
    public string Hash { get; set; } = null!;

    /// <summary>Dependency names still missing at the time of the last attempt.</summary>
    public string[] MissingDependencies { get; set; } = [];

    /// <summary>Timestamp of the last build attempt (UTC).</summary>
    public DateTime LastAttempt { get; set; }

    /// <summary>
    /// The build waits for missing files: it runs again when one of them is added to the dataset, not on a timer.
    /// </summary>
    public bool AwaitingFiles { get; set; }

    /// <summary>The artifacts of the entry's build that exist, absent when there are none.</summary>
    [JsonPropertyName("artifacts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArtifactLinkDto[]? Artifacts { get; set; }
}
