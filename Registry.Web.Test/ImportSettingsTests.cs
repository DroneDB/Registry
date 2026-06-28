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
}
