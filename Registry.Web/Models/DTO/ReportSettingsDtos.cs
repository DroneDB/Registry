using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

/// <summary>
/// A report template of an organization. The members are nullable on the wire so that a missing one is refused by the
/// validator with a message of its own, not by the model binder; what the Registry answers always has them all.
/// </summary>
public class ReportTemplateDto
{
    /// <summary>The identifier (<c>^[a-z0-9-]{1,32}$</c>), unique in the organization; part of the name of the logo file.</summary>
    public string? Id { get; set; }

    /// <summary>The display name, 1 to 60 characters.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The name of the logo file, or null. Only the logo endpoints set it: a value sent with the settings is ignored.
    /// </summary>
    public string? Logo { get; set; }

    /// <summary>The main colour, <c>#rrggbb</c>.</summary>
    public string? Primary { get; set; }

    /// <summary>The accent colour, <c>#rrggbb</c>.</summary>
    public string? Accent { get; set; }

    /// <summary><c>A4</c> or <c>Letter</c>.</summary>
    public string? Paper { get; set; }

    /// <summary><c>portrait</c> or <c>landscape</c>.</summary>
    public string? Orientation { get; set; }

    /// <summary>The text of the page header, at most 200 characters.</summary>
    public string? HeaderText { get; set; }

    /// <summary>The text of the page footer, at most 200 characters.</summary>
    public string? FooterText { get; set; }

    /// <summary>The disclaimer (markdown), at most 5000 characters.</summary>
    public string? Disclaimer { get; set; }

    /// <summary>The kinds of the sections a new report has (cover, summary, overview, tables, items, notes).</summary>
    public List<string>? DefaultSections { get; set; }
}

/// <summary>A category or a severity of the annotation schema.</summary>
public class ReportSchemaChoiceDto
{
    /// <summary>The identifier (<c>^[a-z0-9-]{1,32}$</c>), unique in its list.</summary>
    public string? Id { get; set; }

    /// <summary>The display name, 1 to 60 characters.</summary>
    public string? Name { get; set; }

    /// <summary>The colour, <c>#rrggbb</c>.</summary>
    public string? Colour { get; set; }
}

/// <summary>A status of the annotation schema.</summary>
public class ReportSchemaStatusDto
{
    /// <summary>The identifier (<c>^[a-z0-9-]{1,32}$</c>), unique in the statuses.</summary>
    public string? Id { get; set; }

    /// <summary>The display name, 1 to 60 characters.</summary>
    public string? Name { get; set; }

    /// <summary>Whether an annotation with this status is closed (never overdue).</summary>
    public bool Closed { get; set; }
}

/// <summary>The annotation schema of an organization: what an annotation can be filed under.</summary>
public class ReportSchemaDto
{
    /// <summary>1 to 50 categories, in display order.</summary>
    public List<ReportSchemaChoiceDto>? Categories { get; set; }

    /// <summary>0 to 10 severities, in display order.</summary>
    public List<ReportSchemaChoiceDto>? Severities { get; set; }

    /// <summary>1 to 10 statuses, in display order.</summary>
    public List<ReportSchemaStatusDto>? Statuses { get; set; }
}

/// <summary>The templates and the schema a report is made with (what a dataset reader gets).</summary>
public class ReportSettingsDto
{
    public List<ReportTemplateDto> Templates { get; set; } = [];

    public ReportSchemaDto Schema { get; set; } = new();
}

/// <summary>The settings of an organization, with the version that a write must name.</summary>
public class OrganizationReportSettingsDto : ReportSettingsDto
{
    /// <summary>0 while the organization has not written any; then the number of the writes.</summary>
    public int Version { get; set; }
}

/// <summary>The body of the write of the report settings of an organization.</summary>
public class ReportSettingsUpdateDto
{
    public List<ReportTemplateDto>? Templates { get; set; }

    public ReportSchemaDto? Schema { get; set; }

    /// <summary>The version the writer read; a write is refused (409) when it is no longer the stored one.</summary>
    public int? Version { get; set; }
}

/// <summary>The answer of a logo upload.</summary>
public class ReportLogoDto
{
    /// <summary>The name of the logo file now set on the template.</summary>
    public string Logo { get; set; } = null!;

    /// <summary>The version of the settings after the upload: the next write names it.</summary>
    public int Version { get; set; }
}

/// <summary>The answer of a logo delete.</summary>
public class ReportSettingsVersionDto
{
    /// <summary>The version of the settings after the change: the next write names it.</summary>
    public int Version { get; set; }
}
