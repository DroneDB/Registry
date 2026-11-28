using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.LibddbNative;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// The two exports of a Gaussian splat on the real library: the training data of an ODM project
/// (<c>DDBExportSplatTrainingData</c>) and the <c>.spz</c> of a splat (<c>DDBExportSplatSpz</c>), with the typed
/// exceptions libddb's error codes map to. The splat is a small <c>.splat</c> file whose build is made by hand
/// (a <c>gsplat/model.rad</c> in the build folder is what libddb reads as a finished build), so no build-lod
/// program is needed.
/// Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~SplatExportContractTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
public class SplatExportContractTests : TestBase
{
    private const int Splats = 40;

    private static readonly NativeDdbWrapper Wrapper = new();
    private static readonly NativeDdbRuntime Runtime = new();

    [OneTimeSetUp]
    public void Setup()
    {
        UseDdbFolder();
        var (gate, message) = CheckAbi();
        if (gate != Gate.Applies)
            Assert.Fail(message);

        Runtime.RegisterProcess();
    }

    // ---------------------------------------------------------------- .spz

    // An antimatter15 .splat of n splats: 32 bytes each (position, scale, color, rotation)
    private static void WriteSplatFile(string path, int count)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        for (var i = 0; i < count; i++)
        {
            writer.Write((float)(i % 10));
            writer.Write((float)(i / 10));
            writer.Write(0f);
            for (var s = 0; s < 3; s++)
                writer.Write(0.01f);
            writer.Write(new byte[] { 128, 130, 132, 255 });
            writer.Write(new byte[] { 255, 128, 128, 128 });
        }
    }

    // A dataset with scene.splat and a.txt, indexed; the splat is built (its model.rad exists) when asked
    private static string SplatDataset(TestArea area, bool built, string file = "scene.splat")
    {
        var root = Path.Combine(area.TestFolder, "dataset");
        Directory.CreateDirectory(root);
        Wrapper.Init(root);
        WriteSplatFile(Path.Combine(root, file), Splats);
        File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");
        Wrapper.Add(root, [Path.Combine(root, file), Path.Combine(root, "a.txt")]);

        if (built)
        {
            var build = Wrapper.GetBuildArtifacts(root, [file]).Single().Build!;
            var model = Path.Combine(build.Root!, "gsplat", "model.rad");
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            File.WriteAllText(model, "x");
        }

        return root;
    }

    private static BuildArtifact? Artifact(EntryBuild build, string role) => build.Artifacts.FirstOrDefault(a => a.Role == role);

    [Test]
    public void ExportSplatSpz_ConvertsTheSplatIntoItsBuildFolder_AndListsItAsAnArtifact()
    {
        using var area = new TestArea(nameof(ExportSplatSpz_ConvertsTheSplatIntoItsBuildFolder_AndListsItAsAnArtifact));
        var root = SplatDataset(area, built: true);

        var export = Wrapper.ExportSplatSpz(root, "scene.splat", CancellationToken.None);

        export.Role.ShouldBe(ArtifactRoles.GsplatExport);
        export.Cached.ShouldBeFalse();
        File.Exists(export.Path).ShouldBeTrue();
        new FileInfo(export.Path).Length.ShouldBeGreaterThan(0);
        Path.GetFileName(export.Path).ShouldBe("export.spz");

        var build = Wrapper.GetBuildArtifacts(root, ["scene.splat"]).Single().Build!;
        var artifact = Artifact(build, ArtifactRoles.GsplatExport).ShouldNotBeNull();
        artifact.DownloadName.ShouldBe("export.spz");
        artifact.Legacy.ShouldBeFalse("an export never replaces the model");
        Path.GetFullPath(Path.Combine(build.Root!, artifact.Path)).ShouldBe(Path.GetFullPath(export.Path));
        Artifact(build, ArtifactRoles.GsplatLod).ShouldNotBeNull();
    }

    [Test]
    public void ExportSplatSpz_ASecondCall_FindsTheFile()
    {
        using var area = new TestArea(nameof(ExportSplatSpz_ASecondCall_FindsTheFile));
        var root = SplatDataset(area, built: true);
        var first = Wrapper.ExportSplatSpz(root, "scene.splat", CancellationToken.None);
        var madeAt = File.GetLastWriteTimeUtc(first.Path);

        var second = Wrapper.ExportSplatSpz(root, "scene.splat", CancellationToken.None);

        second.Cached.ShouldBeTrue();
        second.Path.ShouldBe(first.Path);
        File.GetLastWriteTimeUtc(second.Path).ShouldBe(madeAt, "the file is returned as it is");
    }

    [Test]
    public void ExportSplatSpz_AnEntryThatIsNotBuilt_IsNotFound_SayingSo()
    {
        using var area = new TestArea(nameof(ExportSplatSpz_AnEntryThatIsNotBuilt_IsNotFound_SayingSo));
        var root = SplatDataset(area, built: false);

        var ex = Should.Throw<DdbNotFoundException>(() => Wrapper.ExportSplatSpz(root, "scene.splat", CancellationToken.None));

        ex.Message.ShouldContain("not built");
    }

    [Test]
    public void ExportSplatSpz_AnEntryThatIsNotThere_IsNotFound()
    {
        using var area = new TestArea(nameof(ExportSplatSpz_AnEntryThatIsNotThere_IsNotFound));
        var root = SplatDataset(area, built: true);

        Should.Throw<DdbNotFoundException>(() => Wrapper.ExportSplatSpz(root, "other.splat", CancellationToken.None));
    }

    [Test]
    public void ExportSplatSpz_AnEntryThatIsNotASplat_IsInvalidArguments()
    {
        using var area = new TestArea(nameof(ExportSplatSpz_AnEntryThatIsNotASplat_IsInvalidArguments));
        var root = SplatDataset(area, built: true);

        Should.Throw<DdbInvalidArgumentsException>(() => Wrapper.ExportSplatSpz(root, "a.txt", CancellationToken.None));
    }

    [Test]
    public void ExportSplatSpz_AFormatThatIsNotConverted_IsInvalidArguments()
    {
        using var area = new TestArea(nameof(ExportSplatSpz_AFormatThatIsNotConverted_IsInvalidArguments));
        var root = SplatDataset(area, built: true, file: "scene.ksplat");

        var ex = Should.Throw<DdbInvalidArgumentsException>(() =>
            Wrapper.ExportSplatSpz(root, "scene.ksplat", CancellationToken.None));

        ex.Message.ShouldContain("ksplat", Case.Insensitive);
    }

    [Test]
    public void ExportSplatSpz_ACanceledToken_StopsTheCall_AndWritesNothing()
    {
        using var area = new TestArea(nameof(ExportSplatSpz_ACanceledToken_StopsTheCall_AndWritesNothing));
        var root = SplatDataset(area, built: true);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Should.Throw<DdbCanceledException>(() => Wrapper.ExportSplatSpz(root, "scene.splat", canceled.Token));

        Artifact(Wrapper.GetBuildArtifacts(root, ["scene.splat"]).Single().Build!, ArtifactRoles.GsplatExport)
            .ShouldBeNull("a canceled export writes nothing");
        Wrapper.ExportSplatSpz(root, "scene.splat", CancellationToken.None).Cached.ShouldBeFalse("the next call runs");
    }

    [TestCase("../scene.splat")]
    [TestCase(".ddb/dbase.sqlite")]
    public void ExportSplatSpz_ARefusedEntryPath_IsInvalidArguments(string path)
    {
        using var area = new TestArea(nameof(ExportSplatSpz_ARefusedEntryPath_IsInvalidArguments));
        var root = SplatDataset(area, built: true);

        Should.Throw<DdbInvalidArgumentsException>(() => Wrapper.ExportSplatSpz(root, path, CancellationToken.None));
    }

    // ---------------------------------------------------------------- training data

    private const string Camera = "test cam 1 1 brown 0.8";

    // The smallest JPEG (1 x 1): libddb reads the size of an image and checks it against its camera
    private static readonly byte[] Jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");

    // An ODM project the way OpenDroneMap writes it: cameras.json, odm_report/shots.geojson, images/,
    // odm_georeferencing/coords.txt and the filtered point cloud (a binary PLY)
    private static void WriteOdmProject(string folder, bool coords = true)
    {
        const double easting = 576705, northing = 5188170;
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        Directory.CreateDirectory(Path.Combine(folder, "odm_report"));
        Directory.CreateDirectory(Path.Combine(folder, "odm_georeferencing"));
        Directory.CreateDirectory(Path.Combine(folder, "odm_filterpoints"));

        File.WriteAllText(Path.Combine(folder, "cameras.json"), new JsonObject
        {
            [Camera] = new JsonObject
            {
                ["projection_type"] = "brown", ["width"] = 1, ["height"] = 1, ["focal_x"] = 0.8, ["focal_y"] = 0.8,
                ["c_x"] = 0.01, ["c_y"] = -0.02, ["k1"] = -0.05, ["k2"] = 0.01, ["p1"] = 0.001, ["p2"] = -0.0005, ["k3"] = 0.0
            }
        }.ToJsonString());

        var features = new JsonArray();
        foreach (var (name, dx, rotation) in new[] { ("a.JPG", 10.0, new[] { 1.9, 0.1, -0.05 }), ("b.JPG", 14.0, new[] { 2.2, -0.2, 0.3 }) })
        {
            features.Add(new JsonObject
            {
                ["type"] = "Feature",
                ["properties"] = new JsonObject
                {
                    ["filename"] = name, ["camera"] = "v2 " + Camera, ["focal"] = 0.8, ["width"] = 1, ["height"] = 1,
                    ["translation"] = new JsonArray(easting + dx, northing + 20.0, 120.0),
                    ["rotation"] = new JsonArray(rotation[0], rotation[1], rotation[2])
                },
                ["geometry"] = new JsonObject
                {
                    ["type"] = "Point", ["coordinates"] = new JsonArray(-91.99, 46.84, 120.0)
                }
            });
            File.WriteAllBytes(Path.Combine(folder, "images", name), Jpeg);
        }

        File.WriteAllText(Path.Combine(folder, "odm_report", "shots.geojson"),
            new JsonObject { ["type"] = "FeatureCollection", ["features"] = features }.ToJsonString());

        if (coords)
            File.WriteAllText(Path.Combine(folder, "odm_georeferencing", "coords.txt"),
                "WGS84 UTM 15N\n576705 5188170\n-41.9 -5.4 198.3\n");

        // Four points in front of the cameras, shifted by the offset of coords.txt
        using var ply = File.Create(Path.Combine(folder, "odm_filterpoints", "point_cloud.ply"));
        using var writer = new BinaryWriter(ply);
        writer.Write(System.Text.Encoding.ASCII.GetBytes(
            "ply\nformat binary_little_endian 1.0\nelement vertex 4\nproperty float x\nproperty float y\n" +
            "property float z\nproperty float nx\nproperty float ny\nproperty float nz\nproperty uchar red\n" +
            "property uchar blue\nproperty uchar green\nproperty uchar views\nend_header\n"));
        foreach (var (x, y, z) in new[] { (5.5, 12.25, 101.5), (6.5, 13.25, 102.5), (-7.5, 2.75, 99.5), (20.0, 30.0, 100.0) })
        {
            foreach (var value in new[] { (float)x, (float)y, (float)z, 0f, 0f, 1f })
                writer.Write(value);
            writer.Write(new byte[] { 10, 30, 20, 3 });
        }
    }

    // A dataset that holds the ODM project in odm/ and the folder to export to, outside it
    private static (string Root, string Out) ProjectDataset(TestArea area, bool coords = true)
    {
        var root = Path.Combine(area.TestFolder, "dataset");
        Directory.CreateDirectory(root);
        Wrapper.Init(root);
        WriteOdmProject(Path.Combine(root, "odm"), coords);
        return (root, Path.Combine(area.TestFolder, "out"));
    }

    [Test]
    public void ExportSplatTrainingData_WritesTheSparseModelTheImagesAndTheGeoreference()
    {
        using var area = new TestArea(nameof(ExportSplatTrainingData_WritesTheSparseModelTheImagesAndTheGeoreference));
        var (root, output) = ProjectDataset(area);

        var export = Wrapper.ExportSplatTrainingData(root, "odm", output, null, null, CancellationToken.None);

        export.Images.ShouldBe(2);
        export.Points.ShouldBe(4);
        export.Warnings.ShouldNotBeNull();
        foreach (var bin in new[] { "cameras.bin", "images.bin", "points3D.bin" })
            File.Exists(Path.Combine(output, "sparse", "0", bin)).ShouldBeTrue(bin);
        File.Exists(Path.Combine(output, "images", "a.JPG")).ShouldBeTrue();
        var georef = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "georef.json")))!;
        georef["offset"]!.AsArray().Count.ShouldBe(3);
        ((string)georef["crs"]!).ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void ExportSplatTrainingData_TheMaxPointsOption_ThinsTheModel()
    {
        using var area = new TestArea(nameof(ExportSplatTrainingData_TheMaxPointsOption_ThinsTheModel));
        var (root, output) = ProjectDataset(area);

        var export = Wrapper.ExportSplatTrainingData(root, "odm", output, new SplatTrainingOptions(MaxPoints: 2), null,
            CancellationToken.None);

        export.Points.ShouldBeLessThanOrEqualTo(2);
        export.Images.ShouldBe(2);
    }

    [Test]
    public void ExportSplatTrainingData_ReportsItsPhasesInOrder_ThenFinishesAtOne()
    {
        using var area = new TestArea(nameof(ExportSplatTrainingData_ReportsItsPhasesInOrder_ThenFinishesAtOne));
        var (root, output) = ProjectDataset(area);
        var seen = new List<(double Fraction, string? Phase)>();

        Wrapper.ExportSplatTrainingData(root, "odm", output, null, (fraction, phase) => seen.Add((fraction, phase)),
            CancellationToken.None);

        seen.ShouldNotBeEmpty();
        seen.First().Phase.ShouldBe("cameras");
        seen.Last().Phase.ShouldBe("model");
        seen.Last().Fraction.ShouldBe(1);
        seen.Select(s => s.Fraction).ShouldBe(seen.Select(s => s.Fraction).Order(), "the fraction never goes back");
    }

    [Test]
    public void ExportSplatTrainingData_ACanceledToken_StopsTheCall()
    {
        using var area = new TestArea(nameof(ExportSplatTrainingData_ACanceledToken_StopsTheCall));
        var (root, output) = ProjectDataset(area);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Should.Throw<DdbCanceledException>(() =>
            Wrapper.ExportSplatTrainingData(root, "odm", output, null, null, canceled.Token));

        File.Exists(Path.Combine(output, "sparse", "0", "cameras.bin")).ShouldBeFalse();
    }

    [Test]
    public void ExportSplatTrainingData_ACancelFromTheObserver_StopsTheCall()
    {
        using var area = new TestArea(nameof(ExportSplatTrainingData_ACancelFromTheObserver_StopsTheCall));
        var (root, output) = ProjectDataset(area);
        using var request = new CancellationTokenSource();

        // The observer cancels the request at its first report: libddb asks the callback and stops
        Should.Throw<DdbCanceledException>(() => Wrapper.ExportSplatTrainingData(root, "odm", output, null,
            (_, _) => request.Cancel(), request.Token));
    }

    [Test]
    public void ExportSplatTrainingData_AFolderThatIsNotThere_IsNotFound_NamingIt()
    {
        using var area = new TestArea(nameof(ExportSplatTrainingData_AFolderThatIsNotThere_IsNotFound_NamingIt));
        var (root, output) = ProjectDataset(area);

        var ex = Should.Throw<DdbNotFoundException>(() =>
            Wrapper.ExportSplatTrainingData(root, "nothing-here", output, null, null, CancellationToken.None));

        ex.Message.ShouldContain("nothing-here");
    }

    [TestCase("../odm")]
    [TestCase(".ddb")]
    public void ExportSplatTrainingData_ARefusedFolder_IsInvalidArguments(string folder)
    {
        using var area = new TestArea(nameof(ExportSplatTrainingData_ARefusedFolder_IsInvalidArguments));
        var (root, output) = ProjectDataset(area);

        Should.Throw<DdbInvalidArgumentsException>(() =>
            Wrapper.ExportSplatTrainingData(root, folder, output, null, null, CancellationToken.None));
    }

    [Test]
    public void ExportSplatTrainingData_AProjectWithoutCoords_FailsNamingTheFile()
    {
        using var area = new TestArea(nameof(ExportSplatTrainingData_AProjectWithoutCoords_FailsNamingTheFile));
        var (root, output) = ProjectDataset(area, coords: false);

        var ex = Should.Throw<DdbException>(() =>
            Wrapper.ExportSplatTrainingData(root, "odm", output, null, null, CancellationToken.None));

        ex.Message.ShouldContain("coords.txt");
    }
}
