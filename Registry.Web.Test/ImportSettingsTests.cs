#nullable enable
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="ImportSettings.IsSourceTypeAllowed"/>, the single source-type allow-list
/// policy enforced by both the web layer (ImportManager) and the worker (ImportDatasetTool).
/// </summary>
[TestFixture]
public class ImportSettingsTests
{
    [Test]
    public void IsSourceTypeAllowed_NullAllowList_AllowsAny()
    {
        var settings = new ImportSettings { AllowedSourceTypes = null };
        settings.IsSourceTypeAllowed("registry").ShouldBeTrue();
        settings.IsSourceTypeAllowed("archive-url").ShouldBeTrue();
    }

    [Test]
    public void IsSourceTypeAllowed_EmptyAllowList_AllowsAny()
    {
        var settings = new ImportSettings { AllowedSourceTypes = [] };
        settings.IsSourceTypeAllowed("registry").ShouldBeTrue();
    }

    [Test]
    public void IsSourceTypeAllowed_NonEmptyAllowList_EnforcesMembershipCaseInsensitive()
    {
        var settings = new ImportSettings { AllowedSourceTypes = ["registry"] };
        settings.IsSourceTypeAllowed("registry").ShouldBeTrue();
        settings.IsSourceTypeAllowed("REGISTRY").ShouldBeTrue();
        settings.IsSourceTypeAllowed("archive-url").ShouldBeFalse();
    }

    [TestCase("evil.exe")]
    [TestCase("EVIL.EXE")]
    [TestCase("script.ps1")]
    [TestCase("run.bat")]
    [TestCase("lib.dll")]
    [TestCase("payload.sh")]
    [TestCase("archive/path/nested.exe")]
    public void IsExtensionBlocked_DefaultDenyList_BlocksExecutables(string fileName)
    {
        new ImportSettings().IsExtensionBlocked(fileName).ShouldBeTrue();
    }

    [TestCase("cloud.laz")]
    [TestCase("ortho.tif")]
    [TestCase("model.obj")]
    [TestCase("data.geojson")]
    [TestCase("notes.md")]
    [TestCase("photo.JPG")]
    public void IsExtensionBlocked_DefaultDenyList_AllowsDataTypes(string fileName)
    {
        new ImportSettings().IsExtensionBlocked(fileName).ShouldBeFalse();
    }

    [Test]
    public void IsExtensionBlocked_NoExtensionOrEmpty_NotBlocked()
    {
        var settings = new ImportSettings();
        settings.IsExtensionBlocked("README").ShouldBeFalse();
        settings.IsExtensionBlocked("").ShouldBeFalse();
        settings.IsExtensionBlocked(null).ShouldBeFalse();
    }

    [Test]
    public void IsExtensionBlocked_CustomList_MatchesWithOrWithoutDot()
    {
        var settings = new ImportSettings { BlockedFileExtensions = ["foo", ".bar"] };
        settings.IsExtensionBlocked("a.foo").ShouldBeTrue();
        settings.IsExtensionBlocked("a.bar").ShouldBeTrue();
        settings.IsExtensionBlocked("a.laz").ShouldBeFalse();
    }

    [Test]
    public void EffectiveFileImportCapBytes_UsesDedicatedWhenSet()
    {
        var settings = new ImportSettings { MaxImportSizeBytes = 100, MaxFileImportSizeBytes = 50 };
        settings.EffectiveFileImportCapBytes().ShouldBe(50);
    }

    [Test]
    public void EffectiveFileImportCapBytes_FallsBackToSharedWhenZero()
    {
        var settings = new ImportSettings { MaxImportSizeBytes = 100, MaxFileImportSizeBytes = 0 };
        settings.EffectiveFileImportCapBytes().ShouldBe(100);
    }

    [Test]
    public void EffectiveFileImportCapBytes_BothZero_IsUnlimited()
    {
        new ImportSettings().EffectiveFileImportCapBytes().ShouldBe(0);
    }
}
