#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Adapters.Archives;
using Registry.Adapters.Testing;
using Registry.Ports;
using Registry.Ports.Archives;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Features;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The Hub reads the entry types and the archive extensions from <c>GET /sys/features</c>: the
/// types are libddb's (<c>DDBGetEntryTypes</c>), passed through; the archive extensions are the extractor's own rule.
/// Registry names no type and copies no extension.
/// </summary>
[TestFixture]
public class EntryTypeFeaturesTests
{
    private static async Task<FeaturesDto> Fill(IFeatureSection section)
    {
        var features = new FeaturesDto();
        await section.FillAsync(features);
        return features;
    }

    // The record holds arrays: compared by value as text
    private static string Flat(EntryTypeDto t) =>
        $"{t.Id}|{t.Name}|{t.Buildable}|{t.Thumbnail}|{string.Join(',', t.SidecarExtensions)}|{string.Join(',', t.Roles)}";

    private static EntryTypeFeatures Types(IDdbWrapper ddb) => new(new BuildArtifactResolver(ddb, new FileSystem()));

    [Test]
    public async Task EntryTypes_AreLibddbsContractFixture_InItsOrder()
    {
        var ddb = new FakeDdbWrapper();

        var types = (await Fill(Types(ddb))).EntryTypes.ShouldNotBeNull();

        types.Select(Flat).ShouldBe(ddb.GetEntryTypes().Select(t => $"{(int)t.Id}|{t.Name}|{t.Buildable}|{t.Thumbnail}|" +
            $"{string.Join(',', t.SidecarExtensions)}|{string.Join(',', t.Roles)}"));
        types.Length.ShouldBe(System.Enum.GetValues<EntryType>().Length);
        Flat(types.Single(t => t.Name == "GeoRaster")).ShouldBe("4|GeoRaster|True|file||raster.cog");
        types.Single(t => t.Name == "PointCloud").Thumbnail.ShouldBe("build");
        types.Single(t => t.Name == "Vector").SidecarExtensions.ShouldBe(["cpg", "dbf", "shx", "prj"]);
    }

    [Test]
    public async Task EntryTypes_HaveNoRuleOfRegistrys_ATypeLibddbInventsIsWhatTheHubSees()
    {
        var ddb = new Mock<IDdbWrapper>();
        ddb.Setup(d => d.GetEntryTypes()).Returns(
        [
            new EntryTypeInfo
            {
                Id = (EntryType)99, Name = "Hologram", Buildable = true, Thumbnail = "build",
                SidecarExtensions = ["holo"], Roles = ["hologram.mesh"]
            },
            new EntryTypeInfo { Id = EntryType.Generic, Name = "Generic", Buildable = true, Thumbnail = "none" }
        ]);

        var types = (await Fill(Types(ddb.Object))).EntryTypes.ShouldNotBeNull();

        types.Select(Flat).ShouldBe(["99|Hologram|True|build|holo|hologram.mesh", "2|Generic|True|none||"]);
        ddb.Verify(d => d.GetEntryTypes(), Times.Once);
    }

    [Test]
    public async Task EntryTypes_AreReadOncePerProcess_NotPerFeaturesRequest()
    {
        var ddb = new Mock<FakeDdbWrapper> { CallBase = true };
        var resolver = new BuildArtifactResolver(ddb.Object, new FileSystem());

        await Fill(new EntryTypeFeatures(resolver));
        await Fill(new EntryTypeFeatures(resolver));

        ddb.Verify(d => d.GetEntryTypes(), Times.Once);
    }

    [Test]
    public async Task ArchiveExtensions_AreTheExtractorsRule_WithoutTheDot_LongestFirst()
    {
        var extractor = new SharpCompressArchiveExtractor();

        var extensions = (await Fill(new ArchiveFeatures(extractor))).ArchiveExtensions.ShouldNotBeNull();

        extensions.ShouldBe([.. extractor.SupportedExtensions.Select(e => e.TrimStart('.'))]);
        extensions.ShouldAllBe(e => e.All(c => !char.IsUpper(c)) && !e.StartsWith('.'));
        extensions.ShouldContain("zip");
        extensions.ShouldContain("tar.gz");
        extensions.ToList().IndexOf("tar.gz").ShouldBeLessThan(extensions.ToList().IndexOf("gz"));
        // What the section names is what the extraction accepts, and nothing else is
        extensions.ShouldAllBe(e => extractor.IsSupported("a." + e));
        extractor.IsSupported("a.docx").ShouldBeFalse();
    }

    [Test]
    public async Task ArchiveExtensions_FollowTheExtractor_NotACopy()
    {
        var extractor = new Mock<IArchiveExtractor>();
        extractor.SetupGet(e => e.SupportedExtensions).Returns([".tar.zst", ".zst"]);

        (await Fill(new ArchiveFeatures(extractor.Object))).ArchiveExtensions.ShouldBe(["tar.zst", "zst"]);
    }
}
