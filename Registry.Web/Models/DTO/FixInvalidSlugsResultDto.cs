using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Outcome of renaming organizations and datasets whose slug fails slug validation.
/// </summary>
public class FixInvalidSlugsResultDto
{
    /// <summary>True when nothing was changed and the entries are only the plan.</summary>
    public bool DryRun { get; set; }

    public List<SlugFixEntryDto> Entries { get; set; } = [];
}

public class SlugFixEntryDto
{
    /// <summary>"organization" or "dataset".</summary>
    public string Kind { get; set; }

    /// <summary>Organization slug before the fix (for datasets: the organization they belong to).</summary>
    public string OrganizationSlug { get; set; }

    public string OldSlug { get; set; }

    public string NewSlug { get; set; }

    /// <summary>"planned", "renamed", "skipped" or "failed".</summary>
    public string Status { get; set; }

    public string Reason { get; set; }
}
