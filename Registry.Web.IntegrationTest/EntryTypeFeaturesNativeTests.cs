#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Features;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The <c>entryTypes</c> of <c>GET /sys/features</c> on the real libddb: the section is what <c>DDBGetEntryTypes</c>
/// answers (plan decision D8), for every type of Registry's <see cref="EntryType"/> and with the rules the Hub relies on.
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~EntryTypeFeaturesNativeTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Native)]
public class EntryTypeFeaturesNativeTests
{
    [Test]
    public async Task EntryTypes_ReflectDdbGetEntryTypes()
    {
        var ddb = new NativeDdbWrapper();
        var features = new FeaturesDto();

        await new EntryTypeFeatures(new BuildArtifactResolver(ddb, new FileSystem())).FillAsync(features);

        var types = features.EntryTypes.ShouldNotBeNull();
        // The wrapper's list is DDBGetEntryTypes parsed (LibddbContractTests pins its members): nothing is added,
        // dropped or reinterpreted on the way
        types.Select(t => (t.Id, t.Name, t.Buildable, t.Thumbnail)).ShouldBe(
            ddb.GetEntryTypes().Select(t => ((int)t.Id, t.Name, t.Buildable, t.Thumbnail)));
        foreach (var known in Enum.GetValues<EntryType>())
            types.ShouldContain(t => t.Id == (int)known, $"EntryType.{known}");
        types.ShouldAllBe(t => t.Thumbnail == "none" || t.Thumbnail == "file" || t.Thumbnail == "build");
        var raster = types.Single(t => t.Id == (int)EntryType.GeoRaster);
        (raster.Name, raster.Buildable, raster.Thumbnail).ShouldBe(("GeoRaster", true, "file"));
        raster.SidecarExtensions.ShouldBeEmpty();
        raster.Roles.ShouldBe(["raster.cog"]);
        types.Single(t => t.Id == (int)EntryType.Vector).SidecarExtensions.ShouldBe(["cpg", "dbf", "shx", "prj"]);
        types.Single(t => t.Id == (int)EntryType.PointCloud).Roles.ShouldBe(["pointcloud.copc", "pointcloud.ept"]);
    }
}
