using System.Collections.Generic;
using System.Linq;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Reports;

/// <summary>
/// What an organization that has written no report settings uses: the default annotation schema and the Standard
/// template. The first write of the organization stores the Standard template as one of its own, which it can then edit.
/// </summary>
public static class ReportSettingsDefaults
{
    /// <summary>The identifier of the template every organization has until it writes its own.</summary>
    public const string StandardTemplateId = "standard";

    /// <summary>The kinds of the sections of a report, in the order of a report.</summary>
    public static readonly IReadOnlyList<string> SectionKinds = ["cover", "summary", "overview", "tables", "items", "notes"];

    /// <summary>The paper sizes a template can name.</summary>
    public static readonly IReadOnlyList<string> PaperSizes = ["A4", "Letter"];

    /// <summary>The page orientations a template can name.</summary>
    public static readonly IReadOnlyList<string> Orientations = ["portrait", "landscape"];

    /// <summary>The template of an organization without templates (a new one on each call: the caller may change it).</summary>
    public static ReportTemplateDto StandardTemplate() => new()
    {
        Id = StandardTemplateId,
        Name = "Standard",
        Logo = null,
        Primary = "#0070e0",
        Accent = "#0891b2",
        Paper = "A4",
        Orientation = "portrait",
        HeaderText = "",
        FooterText = "",
        Disclaimer = "",
        DefaultSections = [.. SectionKinds]
    };

    /// <summary>The templates of an organization without any: the Standard one.</summary>
    public static List<ReportTemplateDto> Templates() => [StandardTemplate()];

    /// <summary>The annotation schema of an organization without one (a new one on each call). No colour is orange.</summary>
    public static ReportSchemaDto Schema() => new()
    {
        Categories =
        [
            Choice("general", "General", "#64748b"),
            Choice("defect", "Defect", "#dc2626"),
            Choice("observation", "Observation", "#0070e0"),
            Choice("safety", "Safety", "#eab308"),
            Choice("stockpile", "Stockpile", "#7c3aed")
        ],
        Severities =
        [
            Choice("low", "Low", "#16a34a"),
            Choice("medium", "Medium", "#eab308"),
            Choice("high", "High", "#dc2626"),
            Choice("critical", "Critical", "#7f1d1d")
        ],
        Statuses =
        [
            Status("open", "Open", closed: false),
            Status("in-progress", "In progress", closed: false),
            Status("resolved", "Resolved", closed: true)
        ]
    };

    private static ReportSchemaChoiceDto Choice(string id, string name, string colour) =>
        new() { Id = id, Name = name, Colour = colour };

    private static ReportSchemaStatusDto Status(string id, string name, bool closed) =>
        new() { Id = id, Name = name, Closed = closed };
}
