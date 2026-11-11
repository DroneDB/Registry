using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-RAS-11, NFR-001: every contour request of the Hub is the heavy task <c>raster-contours</c>, so the progress and the
/// Cancel come from the task. The tool refuses a bad request at the submit (400), forwards the options of the values
/// (NoData, formula) and the progress of libddb, keeps the GeoJSON as its artifact and says in the log when the result
/// was cut at the cap.
/// </summary>
[TestFixture]
public class RasterContoursToolTests
{
    private const string Dem = "dem.tif";

    private string _work = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IBuildArtifactResolver> _artifacts = null!;
    private Mock<IHeavyToolExecutionContext> _ctx = null!;
    private RasterContoursTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _work = Path.Combine(Path.GetTempPath(), "contours-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        _ddb = new Mock<IDDB>(MockBehavior.Strict);
        _ddb.Setup(d => d.EntryExists(Dem)).Returns(true);
        _ddb.Setup(d => d.GetEntry(Dem)).Returns(new Entry { Path = Dem, Type = EntryType.GeoRaster, Size = 100 });
        _artifacts = new Mock<IBuildArtifactResolver>(MockBehavior.Strict);
        _ctx = new Mock<IHeavyToolExecutionContext>();
        _ctx.SetupGet(c => c.Ddb).Returns(_ddb.Object);
        _ctx.SetupGet(c => c.WorkDir).Returns(_work);
        _tool = new RasterContoursTool(_artifacts.Object);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_work, true);

    private static HeavyToolRequest Request(Dictionary<string, object?> p, string? path = Dem) =>
        new("raster-contours", "1", "org", "ds", path, JsonSerializer.SerializeToElement(
            p.Where(kv => kv.Value != null).ToDictionary()));

    private static HeavyToolRequest Interval(double interval) =>
        Request(new Dictionary<string, object?> { ["interval"] = interval });

    private Task Validate(HeavyToolRequest request) => _tool.ValidateAsync(request, _ctx.Object, CancellationToken.None);

    private sealed class Reports : IProgress<HeavyToolProgress>
    {
        public List<HeavyToolProgress> Items { get; } = [];
        public void Report(HeavyToolProgress value) => Items.Add(value);
    }

    private const string Result = """{"type":"FeatureCollection","features":[],"featureCap":100000,"featureCount":0,"truncated":false}""";

    // ---- what it is

    [Test]
    public void TheToolIsTheRasterContoursTaskThatKeepsGeoJson()
    {
        _tool.Id.ShouldBe("raster-contours");
        _tool.Version.ShouldBe("1");
        _tool.ProducesArtifact.ShouldBeTrue();
        _tool.ResultExtension.ShouldBe("geojson");
        _tool.RequiredAccess.ShouldBe(HeavyToolPermission.Read);
        _tool.RunsNativeBuild.ShouldBeTrue();
        _tool.AddsDatasetFiles.ShouldBeFalse();
        _tool.InputSchema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["path"]);
    }

    [Test]
    public void ThePlanNamesTheArtifactAfterTheRaster()
    {
        var plan = _tool.Plan(Interval(5), _ctx.Object);

        plan.DefaultFileName.ShouldBe("dem_contours.geojson");
        plan.ContentType.ShouldBe("application/geo+json");
        plan.QuotaKey.ShouldBe("raster-contours");
        plan.EstimatedOutputBytes.ShouldBeNull();
    }

    // ---- the submit

    [Test]
    public async Task AValidRequest_PassesTheSubmit()
    {
        await Validate(Request(new Dictionary<string, object?> { ["count"] = 20, ["minElev"] = 1, ["maxElev"] = 9 }));
    }

    [Test]
    public async Task NoPath_Is400()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            Validate(Request(new Dictionary<string, object?> { ["interval"] = 1 }, path: null)));

        ex.Message.ShouldBe("A source raster 'path' is required.");
    }

    [Test]
    public async Task AMissingRaster_Is400()
    {
        _ddb.Setup(d => d.EntryExists("gone.tif")).Returns(false);

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            Validate(Request(new Dictionary<string, object?> { ["interval"] = 1 }, "gone.tif")));

        ex.Message.ShouldBe("Source raster 'gone.tif' does not exist in the dataset.");
    }

    [TestCase(null, null, null, null, "Either 'interval' or 'count' must be specified")]
    [TestCase(0.0, null, null, null, "'interval' must be > 0")]
    [TestCase(null, 0, null, null, "'count' must be > 0")]
    [TestCase(5.0, null, 9.0, 3.0, "'minElev' must be less than 'maxElev'")]
    public async Task ABadRequest_Is400AtTheSubmit_WithTheMessageOfTheEndpoint(double? interval, int? count,
        double? minElev, double? maxElev, string message)
    {
        var ex = await Should.ThrowAsync<BadRequestException>(() => Validate(Request(new Dictionary<string, object?>
        {
            ["interval"] = interval, ["count"] = count, ["minElev"] = minElev, ["maxElev"] = maxElev
        })));

        ex.Message.ShouldBe(message);
    }

    [Test]
    public async Task ANegativeSimplifyTolerance_Is400()
    {
        var ex = await Should.ThrowAsync<BadRequestException>(() => Validate(Request(
            new Dictionary<string, object?> { ["interval"] = 1, ["simplifyTolerance"] = -1 })));

        ex.Message.ShouldBe("'simplifyTolerance' must be >= 0");
    }

    // ---- the run

    [Test]
    public async Task TheRun_ForwardsTheOptionsAndTheValueOptions_AndKeepsTheResultAsTheArtifact()
    {
        string? path = null;
        RasterAnalysisOptions? seen = null;
        double? interval = null;
        _ddb.Setup(d => d.GenerateContours(Dem, 2.5, null, 1.0, 10.0, 90.0, 0.5, 2, It.IsAny<RasterAnalysisOptions?>(),
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, double?, int?, double, double?, double?, double, int, RasterAnalysisOptions?,
                Action<double, string?>?, CancellationToken>((p, i, _, _, _, _, _, _, o, _, _) =>
            {
                path = p;
                interval = i;
                seen = o;
            })
            .Returns(Result);

        var artifact = await _tool.ExecuteAsync(Request(new Dictionary<string, object?>
        {
            ["interval"] = 2.5, ["baseOffset"] = 1, ["minElev"] = 10, ["maxElev"] = 90, ["simplifyTolerance"] = 0.5,
            ["bandIndex"] = 2, ["nodata"] = -9999, ["formula"] = "CELSIUS"
        }), _ctx.Object, new Reports(), CancellationToken.None);

        path.ShouldBe(Dem);
        interval.ShouldBe(2.5);
        seen.ShouldNotBeNull().Nodata.ShouldBe(-9999);
        seen.Formula.ShouldBe("CELSIUS");
        artifact.ShouldNotBeNull();
        artifact.FileName.ShouldBe("dem_contours.geojson");
        artifact.ContentType.ShouldBe("application/geo+json");
        (await File.ReadAllTextAsync(Path.Combine(_work, artifact.RelativePath))).ShouldBe(Result);
        artifact.SizeBytes.ShouldBe(Encoding.UTF8.GetByteCount(Result));
        artifact.Sha256.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task TheRun_WithoutValueOptions_PassesNone()
    {
        RasterAnalysisOptions? seen = new(Nodata: 1);
        _ddb.Setup(d => d.GenerateContours(Dem, null, 20, 0, null, null, 0, 1, It.IsAny<RasterAnalysisOptions?>(),
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, double?, int?, double, double?, double?, double, int, RasterAnalysisOptions?,
                Action<double, string?>?, CancellationToken>((_, _, _, _, _, _, _, _, o, _, _) => seen = o)
            .Returns(Result);

        await _tool.ExecuteAsync(Request(new Dictionary<string, object?> { ["count"] = 20 }), _ctx.Object, new Reports(),
            CancellationToken.None);

        seen.ShouldBeNull();
    }

    [Test]
    public async Task TheProgressOfLibddb_IsTheProgressOfTheTask_WithALogLinePerPhase()
    {
        _ddb.Setup(d => d.GenerateContours(Dem, 1.0, null, 0, null, null, 0, 1, null,
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, double?, int?, double, double?, double?, double, int, RasterAnalysisOptions?,
                Action<double, string?>?, CancellationToken>((_, _, _, _, _, _, _, _, _, progress, _) =>
            {
                progress!(0.0, "statistics");
                progress(0.05, "levels");
                progress(0.4, "levels");
                progress(0.8, "lines");
            })
            .Returns(Result);
        var reports = new Reports();

        await _tool.ExecuteAsync(Interval(1), _ctx.Object, reports, CancellationToken.None);

        reports.Items.Select(r => (r.Fraction, r.Phase)).ShouldBe(
        [
            (0.0, "statistics"), (0.0, "statistics"), (0.05, "levels"), (0.4, "levels"), (0.8, "lines"), (1.0, "done")
        ]);
        reports.Items.Where(r => r.LogChunk != null).Select(r => r.LogChunk).ShouldBe(
        [
            "Contour lines of 'dem.tif'", "levels (5%)", "lines (80%)", "Contours complete (0 lines)"
        ]);
    }

    [Test]
    public async Task AResultCutAtTheCap_SaysSoInTheLog_AndKeepsTheFlagInTheFile()
    {
        const string cut = """{"type":"FeatureCollection","features":[{"type":"Feature"}],"featureCap":100000,"featureCount":100000,"truncated":true}""";
        _ddb.Setup(d => d.GenerateContours(Dem, 1.0, null, 0, null, null, 0, 1, null,
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Returns(cut);
        var reports = new Reports();

        var artifact = await _tool.ExecuteAsync(Interval(1), _ctx.Object, reports, CancellationToken.None);

        reports.Items.Last().LogChunk.ShouldBe(
            "Contours truncated at 100000 lines: raise the interval or limit the elevation range");
        (await File.ReadAllTextAsync(Path.Combine(_work, artifact!.RelativePath))).ShouldContain("\"truncated\":true");
    }

    [Test]
    public async Task TheCancelTokenOfTheTask_ReachesLibddb_AndACancelEndsTheTask()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;
        _ddb.Setup(d => d.GenerateContours(Dem, 1.0, null, 0, null, null, 0, 1, null,
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, double?, int?, double, double?, double?, double, int, RasterAnalysisOptions?,
                Action<double, string?>?, CancellationToken>((_, _, _, _, _, _, _, _, _, _, ct) =>
            {
                seen = ct;
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            })
            .Returns(Result);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            _tool.ExecuteAsync(Interval(1), _ctx.Object, new Reports(), cts.Token));

        seen.ShouldBe(cts.Token);
        Directory.GetFiles(_work).ShouldBeEmpty("a cancelled generation leaves no result");
    }

    [Test]
    public async Task ARasterOfMoreThanAGigapixel_IsReadFromTheCogOfItsBuild()
    {
        var huge = new Entry
        {
            Path = "huge.tif", Type = EntryType.GeoRaster,
            Properties = new Dictionary<string, JsonElement>
            {
                ["width"] = JsonSerializer.SerializeToElement(40000), ["height"] = JsonSerializer.SerializeToElement(40000)
            }
        };
        _ddb.Setup(d => d.GetEntry("huge.tif")).Returns(huge);
        _artifacts.Setup(a => a.GetRenderSource(_ddb.Object, huge)).Returns("build/h/cog.tif");
        _ddb.Setup(d => d.GenerateContours("build/h/cog.tif", 1.0, null, 0, null, null, 0, 1, null,
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Returns(Result);

        await _tool.ExecuteAsync(Request(new Dictionary<string, object?> { ["interval"] = 1 }, "huge.tif"), _ctx.Object,
            new Reports(), CancellationToken.None);

        _artifacts.VerifyAll();
    }

    [Test]
    public async Task ARasterOfMoreThanAGigapixel_WhoseBuildIsNotThere_FailsTellingToBuild()
    {
        var huge = new Entry
        {
            Path = "huge.tif", Type = EntryType.GeoRaster,
            Properties = new Dictionary<string, JsonElement>
            {
                ["width"] = JsonSerializer.SerializeToElement(40000), ["height"] = JsonSerializer.SerializeToElement(40000)
            }
        };
        _ddb.Setup(d => d.GetEntry("huge.tif")).Returns(huge);
        _artifacts.Setup(a => a.GetRenderSource(_ddb.Object, huge)).Returns((string?)null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _tool.ExecuteAsync(
            Request(new Dictionary<string, object?> { ["interval"] = 1 }, "huge.tif"), _ctx.Object, new Reports(),
            CancellationToken.None));

        ex.Message.ShouldContain("build the dataset first");
    }

    // An entry indexed without its size: libddb's refusal is the fallback, as at the endpoint (RasterAnalysisService)
    [Test]
    public async Task ARasterTooLargeToRead_WithoutItsSizeInTheIndex_IsReadFromTheCogOfItsBuild()
    {
        var big = new Entry { Path = "big.tif", Type = EntryType.GeoRaster };
        _ddb.Setup(d => d.GetEntry("big.tif")).Returns(big);
        _ddb.Setup(d => d.GenerateContours("big.tif", 1.0, null, 0, null, null, 0, 1, null,
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("Could not generate contour lines for this raster.",
                new DdbException("2000000000 pixels, more than the 1000000000 that can be read in one piece: build the dataset first")));
        _artifacts.Setup(a => a.GetRenderSource(_ddb.Object, big)).Returns("build/b/cog.tif");
        _ddb.Setup(d => d.GenerateContours("build/b/cog.tif", 1.0, null, 0, null, null, 0, 1, null,
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Returns(Result);
        var reports = new Reports();

        var artifact = await _tool.ExecuteAsync(Request(new Dictionary<string, object?> { ["interval"] = 1 }, "big.tif"),
            _ctx.Object, reports, CancellationToken.None);

        (await File.ReadAllTextAsync(Path.Combine(_work, artifact!.RelativePath))).ShouldBe(Result);
        reports.Items.Select(r => r.LogChunk).ShouldContain(
            "'big.tif' is too large to read in one piece: reading the COG of its build");
        _artifacts.VerifyAll();
    }

    // Another failure of an entry without its size is the failure of the task, not a read of the build
    [Test]
    public async Task AnotherFailure_OfARasterWithoutItsSize_FailsTheTask()
    {
        var big = new Entry { Path = "big.tif", Type = EntryType.GeoRaster };
        _ddb.Setup(d => d.GetEntry("big.tif")).Returns(big);
        _ddb.Setup(d => d.GenerateContours("big.tif", 1.0, null, 0, null, null, 0, 1, null,
                It.IsAny<Action<double, string?>?>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("Could not generate contour lines for this raster.",
                new DdbException("Band 1 does not exist")));

        await Should.ThrowAsync<InvalidOperationException>(() => _tool.ExecuteAsync(
            Request(new Dictionary<string, object?> { ["interval"] = 1 }, "big.tif"), _ctx.Object, new Reports(),
            CancellationToken.None));

        _artifacts.Verify(a => a.GetRenderSource(It.IsAny<IDDB>(), It.IsAny<Entry>()), Times.Never);
    }

    // ---- the summary of a result

    [Test]
    public void TheSummary_ReadsTheFlagAndTheCap_WithoutTheLines()
    {
        var json = Encoding.UTF8.GetBytes(
            """{"baseOffset":0,"featureCap":100000,"featureCount":7,"features":[{"a":[1,2,{"truncated":true}]}],"truncated":false,"unit":"m"}""");

        var summary = ContourSummary.Read(json);

        summary.Truncated.ShouldBeFalse("a flag inside the lines is not the one of the result");
        summary.FeatureCap.ShouldBe(100000);
        summary.FeatureCount.ShouldBe(7);
    }

    [Test]
    public void TheSummary_OfALibddbThatDoesNotSayIt_IsNotTruncated()
    {
        var summary = ContourSummary.Read(Encoding.UTF8.GetBytes("""{"type":"FeatureCollection","features":[]}"""));

        summary.ShouldBe(new ContourSummary(false, null, null));
        ContourSummary.Read("not json"u8).ShouldBe(default);
    }

    // ---- the options of the endpoint

    [Test]
    public void TheOptionsOfTheTask_AreThoseOfTheEndpoint()
    {
        var p = new RasterContoursTool.Parameters(2.5, null, null, null, null, null, null, -1, "KELVIN", null);

        RasterContoursTool.OptionsOf(p).ShouldBe(new ContourOptions(2.5, null, 0.0, null, null, 0.0, 1, -1, "KELVIN"));
        RasterContoursTool.OptionsOf(p).ValueOptions().ShouldBe(new RasterAnalysisOptions(-1, "KELVIN"));
        RasterContoursTool.OptionsOf(p with { Nodata = null, Formula = " " }).ValueOptions().ShouldBeNull();
    }
}
