#nullable enable
using System;

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
}
