using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.LibddbNative;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// On the real libddb (ABI 2.1 and later): align, merge multispectral and mask borders go through
/// <c>DDBAlignRasterEx</c>, <c>DDBMergeMultispectralEx</c> and <c>DDBMaskBordersEx</c> (FR-PRC-01, FR-XC-07, D-028): an
/// output that exists is refused without overwrite and left as it was, replaced with it, and a failed call with
/// overwrite leaves it byte for byte as it was (the Registry no longer deletes it before the call).
/// Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~NativeOutputReplaceTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
public class NativeOutputReplaceTests : TestBase
{
    private const string BaseTestFolder = nameof(NativeOutputReplaceTests);
    private const string Previous = "the file that was there\n";
    private static readonly string GeoRasterUrl = TestData.Url("testdata/raster/byte.tif");

    // The five single-band UInt16 captures of a MicaSense RedEdge shot
    private static readonly string[] BandUrls =
        [.. Enumerable.Range(1, 5).Select(i => TestData.Url($"multispectral/IMG_0180_{i}.tif"))];

    private static readonly NativeDdbWrapper Wrapper = new();

    private string _geoRaster = null!;
    private string[] _bands = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        UseDdbFolder();
        var (gate, message) = CheckAbi();
        if (gate != Gate.Applies)
            Assert.Fail(message);

        new NativeDdbRuntime().RegisterProcess();
        var cache = Path.Combine(Path.GetTempPath(), BaseTestFolder);
        _geoRaster = TestDownloadCache.GetOrDownload(GeoRasterUrl, cache);
        _bands = [.. BandUrls.Select(u => TestDownloadCache.GetOrDownload(u, cache))];
    }

    [OneTimeTearDown]
    public void DeleteDownloads()
    {
        TestDownloadCache.Evict(_geoRaster);
        foreach (var band in _bands ?? [])
            TestDownloadCache.Evict(band);
    }

    private static string Existing(TestArea area, string name)
    {
        var path = Path.Combine(area.TestFolder, name);
        File.WriteAllText(path, Previous);
        return path;
    }

    private static void ShouldBeATiff(string path)
    {
        // The TIFF byte order mark and magic number, little or big endian
        var header = Encoding.Latin1.GetString(File.ReadAllBytes(path).Take(4).ToArray());
        header.ShouldBeOneOf("II*\0", "MM\0*");
    }

    // ---- mask borders ----

    [Test]
    public void Mask_ExistingOutput_WithoutOverwrite_IsRefused_AndLeftAsItWas()
    {
        using var area = new TestArea(nameof(Mask_ExistingOutput_WithoutOverwrite_IsRefused_AndLeftAsItWas));
        var output = Existing(area, "masked.tif");

        Should.Throw<DdbException>(() => Wrapper.MaskBorders(_geoRaster, output));

        File.ReadAllText(output).ShouldBe(Previous);
    }

    [Test]
    public void Mask_ExistingOutput_WithOverwrite_IsReplaced()
    {
        using var area = new TestArea(nameof(Mask_ExistingOutput_WithOverwrite_IsReplaced));
        var output = Existing(area, "masked.tif");

        Wrapper.MaskBorders(_geoRaster, output, overwrite: true);

        ShouldBeATiff(output);
    }

    [Test]
    public void Mask_FailedRun_WithOverwrite_LeavesTheExistingOutputUnchanged()
    {
        using var area = new TestArea(nameof(Mask_FailedRun_WithOverwrite_LeavesTheExistingOutputUnchanged));
        var output = Existing(area, "masked.tif");
        var notARaster = Path.Combine(area.TestFolder, "not-a-raster.tif");
        File.WriteAllText(notARaster, "not a raster");

        Should.Throw<DdbException>(() => Wrapper.MaskBorders(notARaster, output, overwrite: true));

        File.ReadAllText(output).ShouldBe(Previous);
    }

    // ---- merge multispectral ----

    [Test]
    public void Merge_ExistingOutput_WithoutOverwrite_IsRefused_AndLeftAsItWas()
    {
        using var area = new TestArea(nameof(Merge_ExistingOutput_WithoutOverwrite_IsRefused_AndLeftAsItWas));
        var output = Existing(area, "merged.tif");

        Should.Throw<DdbException>(() => Wrapper.MergeMultispectral(_bands, output));

        File.ReadAllText(output).ShouldBe(Previous);
    }

    [Test]
    public void Merge_ExistingOutput_WithOverwrite_IsReplaced()
    {
        using var area = new TestArea(nameof(Merge_ExistingOutput_WithOverwrite_IsReplaced));
        var output = Existing(area, "merged.tif");

        Wrapper.MergeMultispectral(_bands, output, overwrite: true);

        ShouldBeATiff(output);
    }

    [Test]
    public void Merge_FailedRun_WithOverwrite_LeavesTheExistingOutputUnchanged()
    {
        using var area = new TestArea(nameof(Merge_FailedRun_WithOverwrite_LeavesTheExistingOutputUnchanged));
        var output = Existing(area, "merged.tif");
        var notARaster = Path.Combine(area.TestFolder, "not-a-band.tif");
        File.WriteAllText(notARaster, "not a raster");

        Should.Throw<DdbException>(() => Wrapper.MergeMultispectral([_bands[0], notARaster], output, overwrite: true));

        File.ReadAllText(output).ShouldBe(Previous);
    }

    // ---- align ----

    [Test]
    public void Align_FailedRun_WithOverwrite_LeavesTheExistingOutputUnchanged()
    {
        using var area = new TestArea(nameof(Align_FailedRun_WithOverwrite_LeavesTheExistingOutputUnchanged));
        var output = Existing(area, "aligned.tif");
        var notARaster = Path.Combine(area.TestFolder, "not-a-reference.tif");
        File.WriteAllText(notARaster, "not a raster");

        Should.Throw<DdbException>(() => Wrapper.AlignRaster(_geoRaster, notARaster, output, overwrite: true));

        File.ReadAllText(output).ShouldBe(Previous);
    }
}
