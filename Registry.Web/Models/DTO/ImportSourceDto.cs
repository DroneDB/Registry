#nullable enable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Registry.Ports.DroneDB;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Request to verify (probe) an import source before creating a dataset.
/// </summary>
public class VerifyImportRequestDto
{
    /// <summary>The import source type (e.g. <c>registry</c>, <c>archive-url</c>).</summary>
    [Required]
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Source-specific parameters (string key/value pairs, e.g. url, organization, password).</summary>
    public Dictionary<string, string> Params { get; set; } = new();
}

/// <summary>
/// Result of a verify (probe) operation.
/// </summary>
public class ImportVerifyResultDto
{
    /// <summary>Whether the source is reachable and the dataset can be imported.</summary>
    public bool Reachable { get; set; }

    /// <summary>
    /// Optional human-readable note (e.g. a size caveat). NOTE: must NOT be named "message" or "error":
    /// the web client treats any response body containing those fields as an error, even on HTTP 200.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>Estimated size in bytes when known (compressed size for archives).</summary>
    public long? EstimatedBytes { get; set; }

    /// <summary>Number of files when known.</summary>
    public int? FileCount { get; set; }

    /// <summary>Suggested dataset display name derived from the source.</summary>
    public string? SuggestedName { get; set; }

    /// <summary>Suggested URL-safe dataset slug derived from the source.</summary>
    public string? SuggestedSlug { get; set; }
}

/// <summary>
/// Request to create a dataset and start importing into it.
/// </summary>
public class CreateImportRequestDto
{
    /// <summary>The import source type.</summary>
    [Required]
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Source-specific parameters (string key/value pairs).</summary>
    public Dictionary<string, string> Params { get; set; } = new();

    /// <summary>Desired dataset slug. When omitted it is derived from <see cref="Name"/> or the source.</summary>
    public string? Slug { get; set; }

    /// <summary>Desired dataset display name. When omitted it is derived from the source.</summary>
    public string? Name { get; set; }

    /// <summary>Desired dataset visibility. Defaults to <see cref="Visibility.Private"/>.</summary>
    public Visibility? Visibility { get; set; }
}

/// <summary>
/// Result of a create-and-import operation.
/// </summary>
public class ImportCreateResultDto
{
    /// <summary>The heavy-task identifier tracking the import progress.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>The organization slug.</summary>
    public string OrgSlug { get; set; } = string.Empty;

    /// <summary>The newly created dataset slug.</summary>
    public string DsSlug { get; set; } = string.Empty;

    /// <summary>The dataset URL.</summary>
    public string? DatasetUrl { get; set; }
}
