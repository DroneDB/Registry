#nullable enable
using System.Runtime.CompilerServices;
using System.Text.Json;
using NUnit.Framework;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.HeavyTasks.Tools;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-09 / D4: typed tool parameters read stored task requests exactly as the per-tool readers did.
/// A heavy task's request is a Hangfire job argument (<c>HeavyTaskJobWrapper.Run(…, requestJson, …)</c>),
/// so the requests queued before the release must keep their meaning: a value of another JSON kind
/// is absent (not an error), names are case-sensitive, blank array items are dropped where the old
/// reader dropped them. The JSON below is the stored shape (System.Text.Json of
/// <see cref="HeavyToolRequest"/>, as <c>HeavyTaskJobs.EnqueueHeavyTask</c> writes it).
/// </summary>
[TestFixture]
public class Rq09ToolParametersTests
{
    private static HeavyToolRequest Stored(string toolId, string paramsJson) =>
        JsonSerializer.Deserialize<HeavyToolRequest>(
            $"{{\"ToolId\":\"{toolId}\",\"Version\":\"1\",\"OrgSlug\":\"o\",\"DsSlug\":\"d\",\"Path\":null,\"Params\":{paramsJson}}}")!;

    // ReadParams is pure: the tools' collaborators are not needed to read their parameters
    private static TParams Read<TTool, TParams>(string toolId, string paramsJson)
        where TTool : HeavyToolBase<TParams> where TParams : class =>
        ((TTool)RuntimeHelpers.GetUninitializedObject(typeof(TTool))).Params(Stored(toolId, paramsJson));

    [Test]
    public void ToolParameters_ReadLeniently()
    {
        var p = new ToolParameters(JsonDocument.Parse(
            """{"s":"x","n":12,"big":1e30,"t":true,"f":false,"q":"true","a":["a",1,"  ","b",null],"o":{"k":1},"S":"y"}""").RootElement);

        p.String("s").ShouldBe("x");
        p.String("n").ShouldBeNull("a number is not a string");
        p.String("S").ShouldBe("y");
        p.String("missing").ShouldBeNull();
        p.Bool("t").ShouldBe(true);
        p.Bool("f").ShouldBe(false);
        p.Bool("q").ShouldBeNull("\"true\" is a string, not a boolean");
        p.Long("n").ShouldBe(12);
        p.Long("big").ShouldBeNull("does not fit a long");
        p.Long("s").ShouldBeNull();
        p.Strings("a").ShouldBe(["a", "  ", "b"]);
        p.NonBlankStrings("a").ShouldBe(["a", "b"]);
        p.NonBlankStrings("s").ShouldBeNull();
        p.RawArray("a").ShouldBe("""["a",1,"  ","b",null]""");
        p.Object("o").ShouldNotBeNull().GetProperty("k").GetInt32().ShouldBe(1);
        p.Object("s").ShouldBeNull();
    }

    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("\"text\"")]
    public void ParametersThatAreNotAnObject_ReadAsAbsent(string paramsJson)
    {
        var p = new ToolParameters(JsonDocument.Parse(paramsJson).RootElement);
        p.String("s").ShouldBeNull();
        p.Bool("b").ShouldBeNull();
        p.Strings("a").ShouldBeNull();

        Read<BuildTool, BuildTool.Parameters>("build", paramsJson).Force.ShouldBeNull();
    }

    [Test]
    public void StoredFileUrlImport_KeepsItsMeaning()
    {
        var p = Read<FileUrlImportTool, FileUrlImportTool.Parameters>("import-file",
            """{"url":"https://example.com/a.tif","fileName":"a.tif","folder":"in","overwrite":"true","sizeBytes":"12","username":"u","password":"enc"}""");

        p.Url.ShouldBe("https://example.com/a.tif");
        p.Folder.ShouldBe("in");
        p.Overwrite.ShouldBeNull("the old reader treated anything but JSON true as false");
        p.SizeBytes.ShouldBeNull("a number in a string had no size estimate");
        p.Password.ShouldBe("enc");
    }

    [Test]
    public void StoredBulkDownload_DropsNonStringAndBlankPaths()
    {
        var p = Read<BulkDownloadTool, BulkDownloadTool.Parameters>("bulk-download",
            """{"paths":["a.tif",3,"   ","b/c.laz"],"archiveName":"x.zip"}""");

        p.Paths.ShouldBe(["a.tif", "b/c.laz"]);
        p.ArchiveName.ShouldBe("x.zip");
        Read<BulkDownloadTool, BulkDownloadTool.Parameters>("bulk-download", """{"paths":["  "]}""").Paths
            .ShouldBeNull("no usable path means the whole dataset, as before");
    }

    [Test]
    public void StoredPhotogrammetry_KeepsEveryStringImageAndTheRawOptions()
    {
        var p = Read<PhotogrammetryTool, PhotogrammetryTool.Parameters>("photogrammetry",
            """{"nodeId":"n1","images":["a.jpg"," ",1],"options":[{"name":"fast-orthophoto","value":true}],"createNewDataset":true,"newDatasetVisibility":"PUBLIC"}""");

        p.NodeId.ShouldBe("n1");
        p.Images.ShouldBe(["a.jpg", " "]);
        p.Options.ShouldBe("""[{"name":"fast-orthophoto","value":true}]""");
        p.CreateNewDataset.ShouldBe(true);
        p.NewDatasetVisibility.ShouldBe("PUBLIC");
        p.DestPath.ShouldBeNull();
    }

    [Test]
    public void StoredArchiveExtractAndRescan_ReadTheirBooleans()
    {
        var extract = Read<ArchiveExtractTool, ArchiveExtractTool.Parameters>("archive-extract",
            """{"sourcePath":"a.zip","destPath":"out","deleteArchive":true,"overwrite":1}""");
        extract.DeleteArchive.ShouldBe(true);
        extract.Overwrite.ShouldBeNull();

        var rescan = Read<RescanIndexTool, RescanIndexTool.Parameters>("rescan-index",
            """{"types":"vector","extensions":"dwg","stopOnError":false}""");
        rescan.Types.ShouldBe("vector");
        rescan.Extensions.ShouldBe("dwg");
        rescan.StopOnError.ShouldBe(false);
    }

    [Test]
    public void StoredImportDataset_KeepsItsSourceParametersObject()
    {
        var p = Read<ImportDatasetTool, ImportDatasetTool.Parameters>("import-dataset",
            """{"sourceType":"registry","budgetBytes":1024,"params":{"url":"https://hub.dronedb.app/r/a/b"}}""");

        p.SourceType.ShouldBe("registry");
        p.BudgetBytes.ShouldBe(1024);
        p.SourceParams.ShouldNotBeNull().GetProperty("url").GetString().ShouldBe("https://hub.dronedb.app/r/a/b");
        Read<ImportDatasetTool, ImportDatasetTool.Parameters>("import-dataset", """{"params":"x"}""").SourceParams
            .ShouldBeNull();
    }

    [Test]
    public void StoredRasterTools_ReadTheirStrings()
    {
        var export = Read<RasterExportTool, RasterExportTool.Parameters>("raster-export",
            """{"path":"o.tif","preset":"ndvi","formula":null,"colormap":"rdylgn","fileName":"out.tif"}""");
        export.Preset.ShouldBe("ndvi");
        export.Formula.ShouldBeNull();
        export.Colormap.ShouldBe("rdylgn");
        export.FileName.ShouldBe("out.tif");

        var align = Read<AlignRasterTool, AlignRasterTool.Parameters>("align-raster",
            """{"sourcePath":"a.tif","referencePath":"b.tif","outputPath":"c.tif"}""");
        align.Mode.ShouldBeNull("the tool defaults it");
        align.ReferencePath.ShouldBe("b.tif");

        var merge = Read<MergeMultispectralTool, MergeMultispectralTool.Parameters>("merge-multispectral",
            """{"paths":["b1.tif","","b2.tif"],"outputPath":"m.tif"}""");
        merge.Paths.ShouldBe(["b1.tif", "b2.tif"]);
        merge.OutputPath.ShouldBe("m.tif");
    }
}
