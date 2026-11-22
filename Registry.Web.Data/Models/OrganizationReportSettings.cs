using System;

namespace Registry.Web.Data.Models;

/// <summary>
/// The report settings of an organization: the templates its reports are made from and the annotation schema (the
/// categories, severities and statuses an annotation can have). One row per organization, created by the first write;
/// an organization without a row uses the defaults. Deleted with the organization.
/// </summary>
public class OrganizationReportSettings
{
    /// <summary>Slug of the organization (key, and its foreign key).</summary>
    public string OrganizationSlug { get; set; } = null!;

    /// <summary>The templates, a JSON array.</summary>
    public string Templates { get; set; } = null!;

    /// <summary>The annotation schema, a JSON object.</summary>
    public string Schema { get; set; } = null!;

    /// <summary>
    /// Counts the updates (1 for the first write). A concurrency token: a write that read an older version does not
    /// overwrite a newer one.
    /// </summary>
    public int Version { get; set; }

    /// <summary>When the settings were last written (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>User name of the last writer.</summary>
    public string UpdatedBy { get; set; } = null!;
}
