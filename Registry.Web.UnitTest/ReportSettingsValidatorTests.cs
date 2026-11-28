using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Reports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The rules of the report settings an organization writes: what a valid write is, what comes back normalised, and the
/// field path each refusal names (the Hub shows the message next to that field).
/// </summary>
[TestFixture]
public class ReportSettingsValidatorTests
{
    private static ReportSettingsUpdateDto Valid() => new()
    {
        Templates = ReportSettingsDefaults.Templates(),
        Schema = ReportSettingsDefaults.Schema(),
        Version = 0
    };

    private static BadRequestException Refused(Action<ReportSettingsUpdateDto> change)
    {
        var update = Valid();
        change(update);
        return Should.Throw<BadRequestException>(() => ReportSettingsValidator.Validate(update));
    }

    [Test]
    public void TheDefaults_AreValid()
    {
        var (templates, schema) = ReportSettingsValidator.Validate(Valid());

        templates.Select(t => t.Id).ShouldBe(["standard"]);
        schema.Categories!.Count.ShouldBe(5);
        schema.Severities!.Count.ShouldBe(4);
        schema.Statuses!.Count.ShouldBe(3);
    }

    [Test]
    public void TheDefaults_AreTheOnesOfTheSpecification()
    {
        var schema = ReportSettingsDefaults.Schema();
        schema.Categories!.Select(c => (c.Id, c.Name, c.Colour)).ShouldBe(
        [
            ("general", "General", "#64748b"), ("defect", "Defect", "#dc2626"), ("observation", "Observation", "#0070e0"),
            ("safety", "Safety", "#eab308"), ("stockpile", "Stockpile", "#7c3aed")
        ]);
        schema.Severities!.Select(c => (c.Id, c.Name, c.Colour)).ShouldBe(
        [
            ("low", "Low", "#16a34a"), ("medium", "Medium", "#eab308"), ("high", "High", "#dc2626"),
            ("critical", "Critical", "#7f1d1d")
        ]);
        schema.Statuses!.Select(s => (s.Id, s.Name, s.Closed)).ShouldBe(
            [("open", "Open", false), ("in-progress", "In progress", false), ("resolved", "Resolved", true)]);

        var template = ReportSettingsDefaults.StandardTemplate();
        (template.Id, template.Name, template.Logo, template.Primary, template.Accent, template.Paper, template.Orientation)
            .ShouldBe(("standard", "Standard", null, "#0070e0", "#0891b2", "A4", "portrait"));
        (template.HeaderText, template.FooterText, template.Disclaimer).ShouldBe(("", "", ""));
        template.DefaultSections.ShouldBe(["cover", "summary", "overview", "tables", "items", "notes"]);
        template.BreakdownChart.ShouldBe("bars");
    }

    [TestCase("bars")]
    [TestCase("donuts")]
    public void TheSummaryChart_IsKept(string chart)
    {
        var update = Valid();
        update.Templates![0].BreakdownChart = chart;

        ReportSettingsValidator.Validate(update).Templates[0].BreakdownChart.ShouldBe(chart);
    }

    [Test]
    public void AWriteWithoutTheSummaryChart_HasBars()
    {
        var update = Valid();
        update.Templates![0].BreakdownChart = null;

        ReportSettingsValidator.Validate(update).Templates[0].BreakdownChart.ShouldBe("bars");
    }

    [Test]
    public void AWrite_ComesBackNormalised()
    {
        var update = Valid();
        update.Templates![0].Name = "  Brand  ";
        update.Templates[0].Primary = "#AABBCC";
        update.Templates[0].Logo = "report-logo-standard.png";
        update.Templates[0].HeaderText = null;
        update.Schema!.Categories![0].Colour = "#ABCDEF";

        var (templates, schema) = ReportSettingsValidator.Validate(update);

        templates[0].Name.ShouldBe("Brand");
        templates[0].Primary.ShouldBe("#aabbcc");
        templates[0].HeaderText.ShouldBe("");
        // The logo is the logo endpoints' to set
        templates[0].Logo.ShouldBeNull();
        schema.Categories![0].Colour.ShouldBe("#abcdef");
    }

    [Test]
    public void ASchemaWithoutSeveritiesOrClosedStatus_IsValid()
    {
        var update = Valid();
        update.Schema!.Severities = [];
        update.Schema.Statuses = [new ReportSchemaStatusDto { Id = "open", Name = "Open", Closed = false }];

        Should.NotThrow(() => ReportSettingsValidator.Validate(update));
    }

    [TestCase("standard", true)]
    [TestCase("a", true)]
    [TestCase("a-b-9", true)]
    [TestCase("", false)]
    [TestCase("Upper", false)]
    [TestCase("under_score", false)]
    [TestCase("../x", false)]
    [TestCase("standard\n", false)]
    [TestCase("012345678901234567890123456789012", false)]
    [TestCase("01234567890123456789012345678901", true)]
    [TestCase(null, false)]
    public void TheIdRule(string? id, bool valid) => ReportSettingsValidator.IsValidId(id).ShouldBe(valid);

    [Test]
    public void MissingBodyMembers_AreRefused()
    {
        Should.Throw<BadRequestException>(() => ReportSettingsValidator.Validate(null));
        Refused(u => u.Templates = null).FieldErrors!.ShouldContainKey("templates");
        Refused(u => u.Schema = null).FieldErrors!.ShouldContainKey("schema");
    }

    private static IEnumerable<TestCaseData> RefusedWrites()
    {
        static TestCaseData Case(string name, string path, Action<ReportSettingsUpdateDto> change) =>
            new TestCaseData(path, change).SetName($"ARefusedWrite_{name}_Names_{path}");

        yield return Case("NoTemplates", "templates", u => u.Templates = []);
        yield return Case("TooManyTemplates", "templates", u =>
            u.Templates = Enumerable.Range(0, 21).Select(i => Template($"t{i}")).ToList());
        yield return Case("NullTemplate", "templates[0]", u => u.Templates![0] = null!);
        yield return Case("BadTemplateId", "templates[0].id", u => u.Templates![0].Id = "Bad_Id");
        yield return Case("DuplicateTemplateId", "templates[1].id", u => u.Templates!.Add(Template("standard")));
        yield return Case("EmptyName", "templates[0].name", u => u.Templates![0].Name = "  ");
        yield return Case("LongName", "templates[0].name", u => u.Templates![0].Name = new string('n', 61));
        yield return Case("BadPrimary", "templates[0].primary", u => u.Templates![0].Primary = "blue");
        yield return Case("ShortAccent", "templates[0].accent", u => u.Templates![0].Accent = "#fff");
        yield return Case("BadPaper", "templates[0].paper", u => u.Templates![0].Paper = "Legal");
        yield return Case("BadOrientation", "templates[0].orientation", u => u.Templates![0].Orientation = "diagonal");
        yield return Case("BadSummaryChart", "templates[0].breakdownChart", u => u.Templates![0].BreakdownChart = "pies");
        yield return Case("SummaryChartInCapitals", "templates[0].breakdownChart", u => u.Templates![0].BreakdownChart = "Donuts");
        yield return Case("LongHeader", "templates[0].headerText", u => u.Templates![0].HeaderText = new string('h', 201));
        yield return Case("LongFooter", "templates[0].footerText", u => u.Templates![0].FooterText = new string('f', 201));
        yield return Case("LongDisclaimer", "templates[0].disclaimer",
            u => u.Templates![0].Disclaimer = new string('d', 5001));
        yield return Case("NoSections", "templates[0].defaultSections", u => u.Templates![0].DefaultSections = null);
        yield return Case("UnknownSection", "templates[0].defaultSections[1]",
            u => u.Templates![0].DefaultSections![1] = "appendix");
        yield return Case("RepeatedSection", "templates[0].defaultSections[1]",
            u => u.Templates![0].DefaultSections![1] = "cover");
        yield return Case("NoCategories", "categories", u => u.Schema!.Categories = []);
        yield return Case("TooManyCategories", "categories", u =>
            u.Schema!.Categories = Enumerable.Range(0, 51).Select(i => Choice($"c{i}")).ToList());
        yield return Case("BadCategoryColour", "categories[1].colour", u => u.Schema!.Categories![1].Colour = "red");
        yield return Case("DuplicateCategory", "categories[1].id", u => u.Schema!.Categories![1].Id = "general");
        yield return Case("BadCategoryId", "categories[0].id", u => u.Schema!.Categories![0].Id = "Gen eral");
        yield return Case("EmptyCategoryName", "categories[2].name", u => u.Schema!.Categories![2].Name = "");
        yield return Case("TooManySeverities", "severities", u =>
            u.Schema!.Severities = Enumerable.Range(0, 11).Select(i => Choice($"s{i}")).ToList());
        yield return Case("BadSeverityColour", "severities[3].colour", u => u.Schema!.Severities![3].Colour = "#12345g");
        yield return Case("NoStatuses", "statuses", u => u.Schema!.Statuses = []);
        yield return Case("TooManyStatuses", "statuses", u =>
            u.Schema!.Statuses = Enumerable.Range(0, 11)
                .Select(i => new ReportSchemaStatusDto { Id = $"s{i}", Name = "S" }).ToList());
        yield return Case("DuplicateStatus", "statuses[1].id", u => u.Schema!.Statuses![1].Id = "open");
        yield return Case("MissingStatusName", "statuses[0].name", u => u.Schema!.Statuses![0].Name = null);
    }

    [TestCaseSource(nameof(RefusedWrites))]
    public void ARefusedWrite_NamesTheMemberThatBrokeARule(string path, Action<ReportSettingsUpdateDto> change)
    {
        var refusal = Refused(change);

        refusal.StatusCode.ShouldBe(400);
        refusal.FieldErrors.ShouldNotBeNull().Keys.ShouldBe([path]);
        refusal.FieldErrors[path].Single().ShouldBe(refusal.Message);
    }

    [Test]
    public void TheLimits_AreAccepted()
    {
        var update = Valid();
        update.Templates = Enumerable.Range(0, 20).Select(i => Template($"t{i}")).ToList();
        update.Templates[0].Name = new string('n', 60);
        update.Templates[0].HeaderText = new string('h', 200);
        update.Templates[0].Disclaimer = new string('d', 5000);
        update.Schema!.Categories = Enumerable.Range(0, 50).Select(i => Choice($"c{i}")).ToList();
        update.Schema.Severities = Enumerable.Range(0, 10).Select(i => Choice($"s{i}")).ToList();
        update.Schema.Statuses = Enumerable.Range(0, 10)
            .Select(i => new ReportSchemaStatusDto { Id = $"s{i}", Name = "S" }).ToList();

        Should.NotThrow(() => ReportSettingsValidator.Validate(update));
    }

    private static ReportTemplateDto Template(string id)
    {
        var template = ReportSettingsDefaults.StandardTemplate();
        template.Id = id;
        return template;
    }

    private static ReportSchemaChoiceDto Choice(string id) => new() { Id = id, Name = id, Colour = "#112233" };
}
