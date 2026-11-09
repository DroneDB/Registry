using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <c>ProcessingPlatform:PhotogrammetryExcludedFolders</c> (FR-PRC-41) as the configuration binder reads it: not set, the
/// three defaults; an empty list, none (every file is imported); a list, only its folders. The defaults are not an initial
/// value of the array, which the binder would append the configured items to (and an empty list would leave as it is).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~PhotogrammetryExcludedFoldersSettingTests"
/// </summary>
[TestFixture]
public class PhotogrammetryExcludedFoldersSettingTests
{
    private static ProcessingPlatformSettings Bind(string json)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        var settings = new ProcessingPlatformSettings();
        configuration.GetSection("AppSettings:ProcessingPlatform").Bind(settings);
        return settings;
    }

    [Test]
    public void NotSet_ExcludesTheThreeDefaultFolders()
    {
        var settings = Bind("""{ "AppSettings": { "ProcessingPlatform": { "ArtifactTtlHours": 2 } } }""");

        settings.PhotogrammetryExcludedFolders.ShouldBeNull();
        settings.PhotogrammetryExcludedFoldersOrDefault().ShouldBe(["entwine_pointcloud/", "opensfm/", "odm_meshing/"]);
    }

    [Test]
    public void AnEmptyList_ExcludesNothing()
    {
        var settings = Bind("""{ "AppSettings": { "ProcessingPlatform": { "PhotogrammetryExcludedFolders": [] } } }""");

        settings.PhotogrammetryExcludedFoldersOrDefault().ShouldBeEmpty();
    }

    [Test]
    public void AList_ExcludesOnlyItsFolders()
    {
        var settings = Bind(
            """{ "AppSettings": { "ProcessingPlatform": { "PhotogrammetryExcludedFolders": ["entwine_pointcloud/"] } } }""");

        settings.PhotogrammetryExcludedFoldersOrDefault().ShouldBe(["entwine_pointcloud/"]);
    }
}
