#nullable enable
using System;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Request body for the system cleanup endpoint. Selects the scope:
/// - both null  -> cleanup all datasets in all organizations (background job)
/// - only OrganizationSlug set -> cleanup all datasets in the given organization
/// - both set   -> cleanup the specified single dataset
/// </summary>
public class CleanupBuildRequestDto
{
    /// <summary>
    /// Organization slug. When null, the cleanup runs across all organizations.
    /// </summary>
    public string? OrganizationSlug { get; set; }

    /// <summary>
    /// Dataset slug. When null but OrganizationSlug is set, the cleanup runs
    /// across all datasets in that organization.
    /// </summary>
    public string? DatasetSlug { get; set; }
}
