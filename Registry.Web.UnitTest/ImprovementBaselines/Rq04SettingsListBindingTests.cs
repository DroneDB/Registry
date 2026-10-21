#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Models.Configuration;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-04 golden finding (<c>sys-features</c> / <c>sys-config</c>): <c>datasetThumbnailCandidates</c> listed
/// every name twice. The configuration binder does not replace a collection that already has a value: it
/// appends the configured items to it, so a settings list with a non-empty initializer ended up as
/// initializer + configured list. <c>Import:BlockedFileExtensions</c> had the same defect.
/// Config-bound lists now have no initializer; their defaults are applied by the settings class when the
/// list is not configured (<c>Effective…</c>), like <see cref="AppSettings.DefaultKnownProxies"/>.
/// The binding is in memory: deployments whose own <c>appsettings.json</c> lists these settings get the
/// exact list on the next start, nothing stored needs a repair.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq04SettingsListBindingTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq04SettingsListBindingTests
{
    private const string Section = "AppSettings";

    [Test]
    public void EveryListOfTheDefaultSettings_BindsToExactlyTheConfiguredItems()
    {
        var defaults = DefaultSettingsJson();
        var bound = Bind(defaults.ToString());

        var lists = ((JObject)defaults[Section]!).Descendants().OfType<JArray>().ToArray();
        lists.ShouldNotBeEmpty();

        var different = lists
            .Select(list => (Path: list.Path[(Section.Length + 1)..], Configured: Items(list)))
            .Select(list => (list.Path, list.Configured, Bound: BoundItems(bound, list.Path)))
            .Where(list => !list.Bound.SequenceEqual(list.Configured))
            .Select(list => $"AppSettings:{list.Path}: configured {list.Configured.Length} items, bound {list.Bound.Length}")
            .ToArray();

        different.ShouldBeEmpty("every bound list must be exactly the configured one (no duplicate, no extra item)");
    }

    [Test]
    public void ADeploymentList_ReplacesTheDefaultInsteadOfAddingToIt()
    {
        var bound = Bind("""
            {
              "AppSettings": {
                "DatasetThumbnailCandidates": [ "preview.png" ],
                "Import": { "BlockedFileExtensions": [ ".exe" ] }
              }
            }
            """);

        bound.DatasetThumbnailCandidates.ShouldBe(["preview.png"]);
        bound.EffectiveDatasetThumbnailCandidates().ShouldBe(["preview.png"]);
        var import = bound.Import.ShouldNotBeNull();
        import.BlockedFileExtensions.ShouldBe([".exe"]);
        import.EffectiveBlockedFileExtensions().ShouldBe([".exe"]);
    }

    [Test]
    public void TheCodeDefaults_AreTheListsOfTheDefaultSettings()
    {
        var defaults = (JObject)DefaultSettingsJson()[Section]!;

        AppSettings.DefaultDatasetThumbnailCandidates.ShouldBe(Items((JArray)defaults["DatasetThumbnailCandidates"]!));
        AppSettings.DefaultKnownProxies.ShouldBe(Items((JArray)defaults["KnownProxies"]!));
        ImportSettings.DefaultBlockedFileExtensions.ShouldBe(Items((JArray)defaults["Import"]!["BlockedFileExtensions"]!));
    }

    [TestCase("""{ "AppSettings": { "Import": { "MaxImportSizeBytes": 0 } } }""", TestName = "UnsetLists_UseTheDefaults(absent)")]
    [TestCase("""{ "AppSettings": { "DatasetThumbnailCandidates": null, "KnownProxies": null, "Import": { "BlockedFileExtensions": null } } }""",
        TestName = "UnsetLists_UseTheDefaults(null)")]
    [TestCase("""{ "AppSettings": { "DatasetThumbnailCandidates": [], "KnownProxies": [], "Import": { "BlockedFileExtensions": [] } } }""",
        TestName = "UnsetLists_UseTheDefaults(empty, as before: the binder kept the initializer)")]
    public void UnsetLists_UseTheDefaults(string json)
    {
        var bound = Bind(json);

        bound.EffectiveDatasetThumbnailCandidates().ShouldBe(AppSettings.DefaultDatasetThumbnailCandidates);
        bound.EffectiveKnownProxies().ShouldBe(AppSettings.DefaultKnownProxies);
        bound.Import.ShouldNotBeNull().EffectiveBlockedFileExtensions().ShouldBe(ImportSettings.DefaultBlockedFileExtensions);
        new ImportSettings().IsExtensionBlocked("setup.exe").ShouldBeTrue();
    }

    /// <summary>The embedded <c>appsettings-default.json</c>: what a new deployment's <c>appsettings.json</c> starts from.</summary>
    private static JObject DefaultSettingsJson()
    {
        var assembly = typeof(AppSettings).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(MagicStrings.AppSettingsDefaultFileName, StringComparison.Ordinal));

        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return JObject.Parse(reader.ReadToEnd());
    }

    /// <summary>Binds <paramref name="json"/> as the host does (<c>GetSection("AppSettings").Get&lt;AppSettings&gt;()</c>).</summary>
    private static AppSettings Bind(string json) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build()
            .GetSection(Section)
            .Get<AppSettings>()
            .ShouldNotBeNull();

    private static string[] Items(JArray list) => [.. list.Select(item => item.ToString())];

    /// <summary>The bound list at <paramref name="path"/> (dotted, as in the JSON); an unset list has no item.</summary>
    private static string[] BoundItems(AppSettings settings, string path)
    {
        object? value = settings;
        foreach (var name in path.Split('.'))
        {
            var property = value?.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            property.ShouldNotBeNull($"no settings property for '{path}'");
            value = property.GetValue(value);
        }

        return value is IEnumerable items ? [.. items.Cast<object>().Select(item => item.ToString()!)] : [];
    }
}
