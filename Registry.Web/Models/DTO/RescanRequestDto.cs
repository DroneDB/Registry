namespace Registry.Web.Models.DTO;

/// <summary>
/// Request body for the system rescan endpoint. Selects the scope like the cleanup endpoint:
/// - both slugs null -> every dataset in every organization
/// - only OrganizationSlug set -> every dataset in that organization
/// - both set -> that dataset
/// and the entries to rescan: an entry must match one of <see cref="Types"/> (when set)
/// and one of <see cref="Extensions"/> (when set). At least one filter is required.
/// </summary>
public class RescanRequestDto
{
    /// <summary>Organization slug, or null for all organizations.</summary>
    public string? OrganizationSlug { get; set; }

    /// <summary>Dataset slug, or null for all datasets of the scope. Requires OrganizationSlug.</summary>
    public string? DatasetSlug { get; set; }

    /// <summary>Entry types to rescan (e.g. ["vector", "georaster"]), or null for all types.</summary>
    public string[]? Types { get; set; }

    /// <summary>File extensions to rescan (e.g. ["dwg", ".dxf"]; case and leading dot are ignored), or null for all.</summary>
    public string[]? Extensions { get; set; }

    /// <summary>Whether a dataset rescan stops at its first entry error. Default false.</summary>
    public bool StopOnError { get; set; }
}

/// <summary>Result of the system rescan endpoint.</summary>
public class RescanJobDto
{
    /// <summary>
    /// Hangfire id of the background job that finds the datasets with matching entries and
    /// submits a rescan-index task for each of them (visible in each dataset's tasks).
    /// </summary>
    public string JobId { get; set; } = string.Empty;
}
