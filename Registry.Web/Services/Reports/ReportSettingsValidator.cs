using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Reports;

/// <summary>
/// The rules of the report settings an organization writes (the templates and the annotation schema). A write that
/// breaks one is refused with a 400 that names it, before anything is stored: the <c>errors</c> member of the problem
/// has the path of the member that broke it (<c>templates[0].name</c>, <c>categories[1].colour</c>; the schema members
/// are at the top, without <c>schema.</c>). What passes comes back normalised (trimmed names, lower-case colours, a
/// template without the logo the writer sent, which only the logo endpoints set).
/// </summary>
public static partial class ReportSettingsValidator
{
    /// <summary>The most templates an organization can have.</summary>
    public const int MaxTemplates = 20;

    /// <summary>The longest name of a template, a category, a severity or a status.</summary>
    public const int MaxNameLength = 60;

    /// <summary>The longest header and footer text of a template.</summary>
    public const int MaxPageTextLength = 200;

    /// <summary>The longest disclaimer of a template.</summary>
    public const int MaxDisclaimerLength = 5000;

    public const int MinCategories = 1;
    public const int MaxCategories = 50;
    public const int MaxSeverities = 10;
    public const int MinStatuses = 1;
    public const int MaxStatuses = 10;

    /// <summary>The rule of every identifier of the settings (templates and schema entries).</summary>
    public const string IdRule = "^[a-z0-9-]{1,32}$";

    // \z, not $: a .NET $ also matches before a final newline, so "standard\n" would pass as an id (and reach a file name)
    [GeneratedRegex(@"^[a-z0-9-]{1,32}\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}\z")]
    private static partial Regex ColourPattern();

    /// <summary>Whether <paramref name="id"/> is a valid identifier (the same rule for the id of a template in a route).</summary>
    public static bool IsValidId(string? id) => id != null && IdPattern().IsMatch(id);

    /// <summary>
    /// The templates and the schema of <paramref name="update"/>, validated and normalised.
    /// </summary>
    /// <exception cref="BadRequestException">A member is missing or breaks a rule; its path is in the field errors.</exception>
    public static (List<ReportTemplateDto> Templates, ReportSchemaDto Schema) Validate(ReportSettingsUpdateDto? update)
    {
        if (update == null)
            throw new BadRequestException("The report settings are required");

        if (update.Templates == null)
            throw Refuse("templates", "templates are required");

        if (update.Schema == null)
            throw Refuse("schema", "schema is required");

        return (ValidateTemplates(update.Templates), ValidateSchema(update.Schema));
    }

    /// <summary>The 400 for the member at <paramref name="path"/>: the message, and the member in the field errors.</summary>
    public static BadRequestException Refuse(string path, string message) =>
        new(message) { FieldErrors = new Dictionary<string, string[]> { [path] = [message] } };

    private static List<ReportTemplateDto> ValidateTemplates(List<ReportTemplateDto> templates)
    {
        if (templates.Count == 0)
            throw Refuse("templates", "At least one template is required");

        if (templates.Count > MaxTemplates)
            throw Refuse("templates", $"An organization can have at most {MaxTemplates} templates");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ReportTemplateDto>(templates.Count);
        for (var i = 0; i < templates.Count; i++)
        {
            var template = templates[i];
            var at = $"templates[{i}]";
            if (template == null)
                throw Refuse(at, "A template cannot be null");

            var id = RequireId(template.Id, $"{at}.id", "template");
            if (!seen.Add(id))
                throw Refuse($"{at}.id", $"The template id '{id}' is used more than once");

            var label = $"Template '{id}'";
            result.Add(new ReportTemplateDto
            {
                Id = id,
                Name = RequireName(template.Name, $"{at}.name", label),
                // Only the logo endpoints set it: the manager gives the stored one back
                Logo = null,
                Primary = RequireColour(template.Primary, $"{at}.primary", $"{label}: primary"),
                Accent = RequireColour(template.Accent, $"{at}.accent", $"{label}: accent"),
                Paper = RequireOneOf(template.Paper, ReportSettingsDefaults.PaperSizes, $"{at}.paper", $"{label}: paper"),
                Orientation = RequireOneOf(template.Orientation, ReportSettingsDefaults.Orientations,
                    $"{at}.orientation", $"{label}: orientation"),
                HeaderText = OptionalText(template.HeaderText, MaxPageTextLength, $"{at}.headerText", $"{label}: headerText"),
                FooterText = OptionalText(template.FooterText, MaxPageTextLength, $"{at}.footerText", $"{label}: footerText"),
                Disclaimer = OptionalText(template.Disclaimer, MaxDisclaimerLength, $"{at}.disclaimer", $"{label}: disclaimer"),
                DefaultSections = ValidateSections(template.DefaultSections, $"{at}.defaultSections", label),
                // A client older than the choice sends none: its templates keep the bars they had
                BreakdownChart = template.BreakdownChart == null
                    ? ReportSettingsDefaults.DefaultBreakdownChart
                    : RequireOneOf(template.BreakdownChart, ReportSettingsDefaults.BreakdownCharts,
                        $"{at}.breakdownChart", $"{label}: breakdownChart")
            });
        }

        return result;
    }

    private static List<string> ValidateSections(List<string>? sections, string path, string label)
    {
        if (sections == null)
            throw Refuse(path, $"{label}: defaultSections is required");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sections.Count; i++)
        {
            var kind = sections[i];
            if (kind == null || !ReportSettingsDefaults.SectionKinds.Contains(kind))
                throw Refuse($"{path}[{i}]",
                    $"{label}: a section must be one of {string.Join(", ", ReportSettingsDefaults.SectionKinds)}");

            if (!seen.Add(kind))
                throw Refuse($"{path}[{i}]", $"{label}: the section '{kind}' is used more than once");
        }

        return [.. sections];
    }

    private static ReportSchemaDto ValidateSchema(ReportSchemaDto schema) => new()
    {
        Categories = ValidateChoices(schema.Categories, "categories", MinCategories, MaxCategories),
        Severities = ValidateChoices(schema.Severities, "severities", 0, MaxSeverities),
        Statuses = ValidateStatuses(schema.Statuses)
    };

    private static List<ReportSchemaChoiceDto> ValidateChoices(List<ReportSchemaChoiceDto>? choices, string list, int min,
        int max)
    {
        CheckCount(choices?.Count, list, min, max);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ReportSchemaChoiceDto>(choices!.Count);
        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var at = $"{list}[{i}]";
            if (choice == null)
                throw Refuse(at, $"An entry of {list} cannot be null");

            var id = RequireUnique(choice.Id, ids, $"{at}.id", list);
            result.Add(new ReportSchemaChoiceDto
            {
                Id = id,
                Name = RequireName(choice.Name, $"{at}.name", $"{list} '{id}'"),
                Colour = RequireColour(choice.Colour, $"{at}.colour", $"{list} '{id}': colour")
            });
        }

        return result;
    }

    private static List<ReportSchemaStatusDto> ValidateStatuses(List<ReportSchemaStatusDto>? statuses)
    {
        CheckCount(statuses?.Count, "statuses", MinStatuses, MaxStatuses);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ReportSchemaStatusDto>(statuses!.Count);
        for (var i = 0; i < statuses.Count; i++)
        {
            var status = statuses[i];
            var at = $"statuses[{i}]";
            if (status == null)
                throw Refuse(at, "An entry of statuses cannot be null");

            var id = RequireUnique(status.Id, ids, $"{at}.id", "statuses");
            result.Add(new ReportSchemaStatusDto
            {
                Id = id, Name = RequireName(status.Name, $"{at}.name", $"statuses '{id}'"), Closed = status.Closed
            });
        }

        return result;
    }

    private static void CheckCount(int? count, string list, int min, int max)
    {
        if (count == null)
            throw Refuse(list, $"{list} is required");

        if (count < min || count > max)
            throw Refuse(list, min == 0
                ? $"{list} can have at most {max} entries"
                : $"{list} must have {min} to {max} entries");
    }

    private static string RequireUnique(string? id, HashSet<string> seen, string path, string list)
    {
        var valid = RequireId(id, path, list);
        if (!seen.Add(valid))
            throw Refuse(path, $"The id '{valid}' is used more than once in {list}");
        return valid;
    }

    private static string RequireId(string? id, string path, string owner)
    {
        if (!IsValidId(id))
            throw Refuse(path, $"The id of a {owner} must match {IdRule}");
        return id!;
    }

    private static string RequireName(string? name, string path, string label)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength)
            throw Refuse(path, $"{label}: the name must be 1 to {MaxNameLength} characters");
        return trimmed;
    }

    private static string RequireColour(string? colour, string path, string label)
    {
        if (colour == null || !ColourPattern().IsMatch(colour))
            throw Refuse(path, $"{label} must be a colour like #0070e0");
        return colour.ToLowerInvariant();
    }

    private static string RequireOneOf(string? value, IReadOnlyList<string> allowed, string path, string label)
    {
        if (value == null || !allowed.Contains(value))
            throw Refuse(path, $"{label} must be one of {string.Join(", ", allowed)}");
        return value;
    }

    private static string OptionalText(string? text, int max, string path, string label)
    {
        if (text != null && text.Length > max)
            throw Refuse(path, $"{label} can be at most {max} characters");
        return text ?? "";
    }
}
