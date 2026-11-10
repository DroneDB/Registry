using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Configuration;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-ADM-14: a setting that appsettings-default.json does not ship (the whole ProcessingPlatform section) shows the value
/// the Registry runs with, which is the default of its settings class, not 0 or False.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ConfigurationEffectiveDefaultsTests"
/// </summary>
[TestFixture]
public class ConfigurationEffectiveDefaultsTests
{
    private static ConfigurationFieldDataDto FieldOf(IConfigurationSectionBuilder builder, string section, string key) =>
        builder.Build().Select(p => p.Section).First(s => s.Name == section && s.Title != "Tool Gating")
            .Fields.Single(f => f.Key == key);

    private static ProcessingSections Processing(Dictionary<string, string?> settings)
    {
        var view = new ConfigurationView(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var budget = HostResourceBudget.Create(InstanceType.Default, new HostFacts(8, 16L << 30),
            new HostResourceOptions(), new DdbRuntimeOptions(), _ => null);
        return new ProcessingSections(view, budget);
    }

    [Test]
    public void ProcessingPlatform_WithoutASection_ShowsTheDefaultsOfTheClass_AsCurrentAndDefault()
    {
        var sections = Processing([]);

        var threshold = FieldOf(sections, "ProcessingPlatform", "BulkDownloadAsyncThresholdBytes");
        threshold.CurrentValue.ShouldBe("524288000");
        threshold.DefaultValue.ShouldBe("524288000");
        threshold.IsSet.ShouldBeFalse();

        FieldOf(sections, "ProcessingPlatform", "DedupEnabled").CurrentValue.ShouldBe("True");
        FieldOf(sections, "ProcessingPlatform", "MaxConcurrentTasksPerUser").CurrentValue.ShouldBe("3");
        FieldOf(sections, "ProcessingPlatform", "MaxConcurrentTasksPerUser").DefaultValue.ShouldBe("3");
        FieldOf(sections, "ProcessingPlatform", "MaxArchiveExtractSizeBytes").CurrentValue.ShouldBe("5368709120");
    }

    [Test]
    public void ProcessingPlatform_WithoutASection_ShowsTheJsonDefaults_AsCurrentToo_NotNull()
    {
        var sections = Processing([]);

        var budget = FieldOf(sections, "ProcessingPlatform", "OrgDailyOutputBytes");
        budget.CurrentValue.ShouldBe("{\n  \"default\": 107374182400\n}");
        budget.CurrentValue.ShouldBe(budget.DefaultValue);
        var nodes = FieldOf(sections, "ProcessingPlatform", "NodeOdx");
        nodes.CurrentValue.ShouldBe("[]");
        nodes.CurrentValue.ShouldBe(nodes.DefaultValue);
        // Nothing is configured: the value is the default, not a set one
        budget.IsSet.ShouldBeFalse();
        nodes.IsSet.ShouldBeFalse();
    }

    [Test]
    public void ProcessingPlatform_ConfiguredValue_WinsOverTheDefault_AndTheDefaultStaysTheClasss()
    {
        var sections = Processing(new Dictionary<string, string?>
        {
            ["AppSettings:ProcessingPlatform:BulkDownloadAsyncThresholdBytes"] = "1000",
            ["AppSettings:ProcessingPlatform:DedupEnabled"] = "false"
        });

        var threshold = FieldOf(sections, "ProcessingPlatform", "BulkDownloadAsyncThresholdBytes");
        threshold.CurrentValue.ShouldBe("1000");
        threshold.DefaultValue.ShouldBe("524288000");
        FieldOf(sections, "ProcessingPlatform", "DedupEnabled").CurrentValue.ShouldBe("False");
        FieldOf(sections, "ProcessingPlatform", "DedupEnabled").DefaultValue.ShouldBe("True");
    }

    [Test]
    public void ProcessingPlatform_SaysWhatZeroMeans_WhereItIsNoLimit()
    {
        FieldOf(Processing([]), "ProcessingPlatform", "MaxArchiveExtractSizeBytes").Description!.ShouldContain("0 = no limit");
    }
}
