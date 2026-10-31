using Registry.Web.Test.Support;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class RescanIndexToolTest
{
    private Mock<IDDB> _ddb = null!;
    private Mock<IDatasetCacheInvalidator> _cacheInvalidator = null!;
    private RescanIndexTool _tool = null!;

    [SetUp]
    public void Setup()
    {
        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(x => x.DatasetFolderPath).Returns("/data/ds");
        _cacheInvalidator = new Mock<IDatasetCacheInvalidator>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_cacheInvalidator.Object);
        services.AddScoped<RescanRebuild>();
        _tool = new RescanIndexTool(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    [Test]
    public async Task Execute_ExtensionFilter_RebuildsOnlyRescannedBuildableEntries()
    {
        _ddb.Setup(x => x.RescanIndex(new RescanOptions(null, "dwg", false)))
            .Returns([
                new RescanResult { Path = "site.dwg", Success = true, Hash = "h1" },
                new RescanResult { Path = "fails.dwg", Success = true, Hash = "h2" },
                new RescanResult { Path = "plain.dwg", Success = true, Hash = "h3" },
                new RescanResult { Path = "broken.dwg", Success = false, Error = "bad file" }
            ]);
        _ddb.Setup(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> paths) => paths
            .Select(p => p != "plain.dwg"
                ? TestBuilds.Build("/datasets/ds", p, EntryType.Vector, "h-" + p, BuildState.None)
                : TestBuilds.NotBuildable(p)).ToArray());
        _ddb.Setup(x => x.Build("fails.dwg", null, true)).Throws(new InvalidOperationException("cannot build"));

        await Execute(new { extensions = ".DWG" });

        _ddb.Verify(x => x.ClearBuildCache(), Times.Never);
        _ddb.Verify(x => x.Build("site.dwg", null, true), Times.Once);
        _ddb.Verify(x => x.Build("fails.dwg", null, true), Times.Once);
        _ddb.Verify(x => x.Build(It.Is<string>(p => p == "plain.dwg" || p == "broken.dwg" || p == null),
            It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _cacheInvalidator.Verify(x => x.InvalidateAllDatasetCachesAsync("org", "ds"), Times.Once);
    }

    [Test]
    public async Task Execute_FilterWithoutMatches_TouchesNothing()
    {
        _ddb.Setup(x => x.RescanIndex(new RescanOptions("vector", null, false))).Returns([]);

        await Execute(new { types = "Vector" });

        _ddb.Verify(x => x.ClearBuildCache(), Times.Never);
        _ddb.Verify(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _cacheInvalidator.Verify(x => x.InvalidateAllDatasetCachesAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task Execute_NoFilter_ClearsBuildCacheAndRebuildsDataset()
    {
        _ddb.Setup(x => x.RescanIndex(new RescanOptions(null, null, false)))
            .Returns([new RescanResult { Path = "a.tif", Success = true, Hash = "h1" }]);

        await Execute(new { });

        _ddb.Verify(x => x.ClearBuildCache(), Times.Once);
        // A null path builds the whole dataset; the port declares the parameter non-nullable.
        _ddb.Verify(x => x.Build(null!, null, true), Times.Once);
        _ddb.Verify(x => x.Build("a.tif", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _cacheInvalidator.Verify(x => x.InvalidateAllDatasetCachesAsync("org", "ds"), Times.Once);
    }

    [TestCase("{\"types\":\"nope\"}")]
    [TestCase("{\"extensions\":\"*.dwg\"}")]
    public void Validate_InvalidFilter_ThrowsArgumentException(string prms)
    {
        var request = new HeavyToolRequest("rescan-index", "1", "org", "ds", null,
            JsonDocument.Parse(prms).RootElement);

        Should.Throw<ArgumentException>(() => _tool.ValidateAsync(request,
            new HeavyToolValidationContext(_ddb.Object, null, NullLogger.Instance), CancellationToken.None));
    }

    private Task<HeavyToolArtifact?> Execute(object prms)
    {
        var request = new HeavyToolRequest("rescan-index", "1", "org", "ds", null,
            JsonSerializer.SerializeToElement(prms));
        var ctx = TestJobs.ExecutionContext(_ddb.Object);
        return _tool.ExecuteAsync(request, ctx, new Progress<HeavyToolProgress>(), CancellationToken.None);
    }
}
