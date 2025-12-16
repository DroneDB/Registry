#nullable enable
namespace Registry.Web.Models.DTO;

/// <summary>
/// DTO representing the result of a dataset rescan operation
/// </summary>
public class RescanResultDto
{
    /// <summary>
    /// Organization slug
    /// </summary>
    public string OrganizationSlug { get; set; } = string.Empty;

    /// <summary>
    /// Dataset slug
    /// </summary>
    public string DatasetSlug { get; set; } = string.Empty;

    /// <summary>
    /// Total number of entries processed
    /// </summary>
    public int TotalProcessed { get; set; }

    /// <summary>
    /// Number of successful rescans
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed rescans
    /// </summary>
    public int ErrorCount { get; set; }

    /// <summary>
    /// Details for each rescanned entry
    /// </summary>
    public RescanEntryResultDto[] Entries { get; set; } = [];
}

/// <summary>
/// DTO representing the result of a single entry rescan
/// </summary>
public class RescanEntryResultDto
{
    /// <summary>
    /// The path of the scanned entry
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The hash of the entry after rescan
    /// </summary>
    public string? Hash { get; set; }

    /// <summary>
    /// Whether the rescan was successful for this entry
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the rescan failed
    /// </summary>
    public string? Error { get; set; }
}
