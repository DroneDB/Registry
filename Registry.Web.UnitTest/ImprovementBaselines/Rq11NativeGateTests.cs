#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-11 (XI-06): every native raster call of the objects slice runs through <see cref="INativeWorkGate"/>, on
/// the heavy lane. Before, 5 calls ran in <c>Task.Run</c> (merge, preview, merge validation, align validation,
/// export) and 11 on the request thread (raster info, metadata, values, profile, volume, stockpiles, contours,
/// the export size estimate): a burst of them ran that many GDAL calls at once, next to the lanes the host
/// budget sized. Each test records the heavy lane's calls in flight while the native call runs.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq11NativeGateTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class Rq11NativeGateTests : IDisposable
{
    private const string OrgSlug = "org";
    private const string DsSlug = "ds";
    private const string Folder = "/datasets/org/ds";
    private const string Hash = "raster1";

    private NativeWorkGate _gate = null!;
    private Mock<IDDB> _ddb = null!;
    private ObjectServices _services = null!;
    private readonly List<int> _inFlight = [];

    [SetUp]
    public void SetUp()
    {
        _gate = new NativeWorkGate(new NativeConcurrencySettings { Heavy = 1, Tiles = 1 });
        _inFlight.Clear();

        var org = new Organization { Slug = OrgSlug, Name = "Org", CreationDate = DateTime.UtcNow, OwnerId = "u" };
        var dataset = new Dataset { Slug = DsSlug, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = org };
        var utils = new Mock<IDatasetResolver>();
        utils.Setup(u => u.GetDatasetAsync(OrgSlug, DsSlug, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dataset);
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);

        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(Folder);
        _ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns<string>(p => Path.Combine(Folder, p));
        foreach (var path in new[] { "ortho.tif", "b1.tif", "b2.tif" })
            _ddb.Setup(d => d.GetEntry(path)).Returns(new Entry { Path = path, Type = EntryType.GeoRaster, Hash = Hash, Size = 1 });
        _ddb.SetupBuilds(TestBuilds.Build(Folder, "ortho.tif", EntryType.GeoRaster, Hash, BuildState.Complete,
            TestBuilds.File(ArtifactRoles.RasterCog, "cog/cog.tif")));
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(OrgSlug, dataset.InternalRef)).Returns(_ddb.Object);

        _services = new ObjectServicesBuilder(TestDb.CreateContext<RegistryContext>())
            .WithDatasets(utils.Object)
            .WithAuthManager(auth.Object)
            .WithDdbManager(ddbManager.Object)
            .WithFileSystem(Mock.Of<IFileSystem>())
            .WithNativeGate(_gate)
            .Build();
    }

    [TearDown]
    public void Dispose() => _gate?.Dispose();

    // The native call records how many heavy-lane calls are running (itself included when it is gated)
    private void Native<T>(Expression<Func<IDDB, T>> call, T result) =>
        _ddb.Setup(call).Returns(() =>
        {
            _inFlight.Add(_gate.InFlight(NativeLane.Heavy));
            return result;
        });

    private void NativeVoid(Expression<Action<IDDB>> call) =>
        _ddb.Setup(call).Callback(() => _inFlight.Add(_gate.InFlight(NativeLane.Heavy)));

    private void ShouldHaveRunInTheHeavyLane(int calls = 1)
    {
        _inFlight.Count.ShouldBe(calls, "the native call ran");
        _inFlight.ShouldAllBe(n => n == 1, "the native call held the heavy lane's slot");
        _gate.InFlight(NativeLane.Heavy).ShouldBe(0, "the slot was released");
    }

    private const string Info = """{"width": 10, "height": 10, "bandCount": 1, "dataType": "Byte"}""";

    private static IEnumerable<TestCaseData> AnalysisCalls()
    {
        TestCaseData Case(string name, Expression<Func<IDDB, string>> native, Func<IRasterAnalysisService, CancellationToken, Task> call) =>
            new TestCaseData(native, call).SetName(name);

        yield return Case("GetRasterInfo", d => d.GetRasterInfo(It.IsAny<string>()),
            (r, ct) => r.GetRasterInfoAsync(OrgSlug, DsSlug, "ortho.tif", ct: ct));
        yield return Case("GetRasterMetadata", d => d.GetRasterMetadata(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            (r, ct) => r.GetRasterMetadataAsync(OrgSlug, DsSlug, "ortho.tif", ct: ct));
        yield return Case("GetRasterValueInfo", d => d.GetRasterValueInfo(It.IsAny<string>()),
            (r, ct) => r.GetRasterValueInfoAsync(OrgSlug, DsSlug, "ortho.tif", ct: ct));
        yield return Case("GetRasterPointValue", d => d.GetRasterPointValue(It.IsAny<string>(), 1, 2),
            (r, ct) => r.GetRasterPointValueAsync(OrgSlug, DsSlug, "ortho.tif", 1, 2, ct: ct));
        yield return Case("GetRasterAreaStats", d => d.GetRasterAreaStats(It.IsAny<string>(), 0, 0, 5, 5),
            (r, ct) => r.GetRasterAreaStatsAsync(OrgSlug, DsSlug, "ortho.tif", 0, 0, 5, 5, ct: ct));
        yield return Case("GetRasterProfile", d => d.GetRasterProfile(It.IsAny<string>(), It.IsAny<string>(), 10),
            (r, ct) => r.GetRasterProfileAsync(OrgSlug, DsSlug, "ortho.tif", "{}", 10, ct: ct));
        yield return Case("CalculateVolume", d => d.CalculateVolume(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), 0),
            (r, ct) => r.CalculateVolumeAsync(OrgSlug, DsSlug, "ortho.tif", "{}", null, 0, ct: ct));
        yield return Case("DetectStockpile", d => d.DetectStockpile(It.IsAny<string>(), 1, 2, 5, 0.5f),
            (r, ct) => r.DetectStockpileAsync(OrgSlug, DsSlug, "ortho.tif", 1, 2, 5, 0.5f, ct: ct));
        yield return Case("DetectAllStockpiles", d => d.DetectAllStockpiles(It.IsAny<string>(), 0.5f, 5, 50),
            (r, ct) => r.DetectAllStockpilesAsync(OrgSlug, DsSlug, "ortho.tif", 0.5f, 5, 50, ct: ct));
        yield return Case("GenerateContours", d => d.GenerateContours(It.IsAny<string>(), 1.0, null, 0, null, null, 0, 1),
            (r, ct) => r.GenerateContoursAsync(OrgSlug, DsSlug, "ortho.tif", new ContourOptions(1.0, null, 0, null, null, 0, 1), ct: ct));
    }

    [TestCaseSource(nameof(AnalysisCalls))]
    public async Task Analysis_RunsInTheHeavyLane(Expression<Func<IDDB, string>> native,
        Func<IRasterAnalysisService, CancellationToken, Task> call)
    {
        Native(native, Info);

        await call(_services.Raster, CancellationToken.None);

        ShouldHaveRunInTheHeavyLane();
    }

    /// <summary>
    /// XI-07 steps 3 and 4: the raster analysis exports, which libddb now lists as cancellable, run under the request's token
    /// (the gate makes it the current <see cref="DdbCancellation"/> of the native call, and the adapter hands it to libddb),
    /// so a request that goes away stops the analysis at its next checkpoint instead of finishing it for nobody.
    /// </summary>
    [TestCaseSource(nameof(AnalysisCalls))]
    public async Task Analysis_RunsUnderTheRequestsToken(Expression<Func<IDDB, string>> native,
        Func<IRasterAnalysisService, CancellationToken, Task> call)
    {
        var seen = new List<CancellationToken>();
        _ddb.Setup(native).Returns(() =>
        {
            seen.Add(DdbCancellation.Current);
            return Info;
        });
        using var request = new CancellationTokenSource();

        await call(_services.Raster, request.Token);

        seen.ShouldNotBeEmpty();
        seen.ShouldAllBe(token => token == request.Token);
    }

    [Test]
    public async Task Export_RunsTheSizeEstimateAndTheExportInTheHeavyLane()
    {
        Native(d => d.GetRasterInfo(It.IsAny<string>()), Info);
        NativeVoid(d => d.ExportRaster(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()));

        await _services.Raster.ExportRasterAsync(OrgSlug, DsSlug, "ortho.tif", new RasterVisualization());

        ShouldHaveRunInTheHeavyLane(calls: 2);
    }

    [Test]
    public async Task Processing_RunsInTheHeavyLane()
    {
        string[] bands = ["b1.tif", "b2.tif"];
        Native(d => d.ValidateMergeMultispectral(It.IsAny<string[]>()), "{}");
        Native(d => d.PreviewMergeMultispectral(It.IsAny<string[]>(), It.IsAny<string?>(), It.IsAny<int>()), [1, 2]);
        NativeVoid(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>()));
        Native(d => d.ValidateAlignRaster(It.IsAny<string>(), It.IsAny<string>()), "{}");

        await _services.Processing.ValidateMergeMultispectralAsync(OrgSlug, DsSlug, bands);
        await _services.Processing.PreviewMergeMultispectralAsync(OrgSlug, DsSlug, bands);
        await _services.Processing.MergeMultispectralAsync(OrgSlug, DsSlug, bands, "merged.tif");
        await _services.Processing.ValidateAlignRasterAsync(OrgSlug, DsSlug, "b1.tif", "b2.tif");

        ShouldHaveRunInTheHeavyLane(calls: 4);
    }
}
