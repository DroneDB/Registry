using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class BuildStatusServiceTest : TestBase
{
    private Mock<IJobIndexQuery> _jobIndexQueryMock;
    private Mock<IDDB> _ddbMock;
    private ILogger<BuildStatusService> _logger;

    private const string OrgSlug = "org";
    private const string DsSlug = "ds";

    [SetUp]
    public void Setup()
    {
        _jobIndexQueryMock = new Mock<IJobIndexQuery>();
        _ddbMock = new Mock<IDDB>();
        _logger = CreateTestLogger<BuildStatusService>();

        _jobIndexQueryMock
            .Setup(x => x.GetByOrgDsAsync(OrgSlug, DsSlug, 0, int.MaxValue, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _ddbMock.Setup(x => x.GetPendingBuildInfo()).Returns([]);
    }

    private BuildStatusService CreateService() => new(_jobIndexQueryMock.Object, _logger);

    private static EntryDto Entry(string path, EntryType type = EntryType.GeoRaster) =>
        new() { Path = path, Type = type };

    [Test]
    public async Task AnnotateAsync_NoBuildableEntries_SkipsAllLookups()
    {
        var entries = new[] { Entry("readme.txt", EntryType.Markdown), Entry("photo.jpg", EntryType.Image) };

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, entries);

        entries.ShouldAllBe(e => e.BuildStatus == null);
        _jobIndexQueryMock.Verify(
            x => x.GetByOrgDsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _ddbMock.Verify(x => x.GetPendingBuildInfo(), Times.Never);
    }

    [Test]
    public async Task AnnotateAsync_ActiveBuildJob_MarksBuilding()
    {
        var entry = Entry("ortho.tif");
        _ddbMock.Setup(x => x.IsBuildable("ortho.tif")).Returns(true);
        _jobIndexQueryMock
            .Setup(x => x.GetByOrgDsAsync(OrgSlug, DsSlug, 0, int.MaxValue, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new JobIndex
                {
                    JobId = "j1", OrgSlug = OrgSlug, DsSlug = DsSlug, Path = "ortho.tif",
                    ToolId = "build", CurrentState = "Processing"
                }
            ]);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBe("building");
        entry.BuildMissingDependencies.ShouldBeNull();
    }

    [Test]
    public async Task AnnotateAsync_PendingWithMissingDeps_MarksPendingAndSurfacesDeps()
    {
        var entry = Entry("layer.shp", EntryType.Vector);
        _ddbMock.Setup(x => x.IsBuildable("layer.shp")).Returns(true);
        _ddbMock.Setup(x => x.GetPendingBuildInfo()).Returns([
            new PendingBuildInfo
            {
                Hash = "h1", Path = "layer.shp", MissingDependencies = ["layer.dbf"], LastAttempt = 123
            }
        ]);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBe("pending");
        entry.BuildMissingDependencies.ShouldBe(["layer.dbf"]);
        // Build completeness is a filesystem check; must not be reached once pending is known.
        _ddbMock.Verify(x => x.IsBuildComplete(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task AnnotateAsync_BuildComplete_LeavesStatusNull()
    {
        var entry = Entry("ortho.tif");
        _ddbMock.Setup(x => x.IsBuildable("ortho.tif")).Returns(true);
        _ddbMock.Setup(x => x.IsBuildComplete("ortho.tif")).Returns(true);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBeNull();
        entry.BuildMissingDependencies.ShouldBeNull();
    }

    [Test]
    public async Task AnnotateAsync_LastJobFailedAndIncomplete_MarksFailed()
    {
        var entry = Entry("ortho.tif");
        _ddbMock.Setup(x => x.IsBuildable("ortho.tif")).Returns(true);
        _ddbMock.Setup(x => x.IsBuildComplete("ortho.tif")).Returns(false);
        _jobIndexQueryMock
            .Setup(x => x.GetByOrgDsAsync(OrgSlug, DsSlug, 0, int.MaxValue, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new JobIndex
                {
                    JobId = "j1", OrgSlug = OrgSlug, DsSlug = DsSlug, Path = "ortho.tif",
                    ToolId = "build", CurrentState = "Failed", CreatedAtUtc = DateTime.UtcNow
                }
            ]);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBe("failed");
    }

    [Test]
    public async Task AnnotateAsync_NothingYet_MarksQueued()
    {
        var entry = Entry("ortho.tif");
        _ddbMock.Setup(x => x.IsBuildable("ortho.tif")).Returns(true);
        _ddbMock.Setup(x => x.IsBuildComplete("ortho.tif")).Returns(false);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBe("queued");
    }

    [Test]
    public async Task AnnotateAsync_NotBuildableEntry_LeavesStatusNull()
    {
        var entry = Entry("weird.tif");
        _ddbMock.Setup(x => x.IsBuildable("weird.tif")).Returns(false);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBeNull();
        _ddbMock.Verify(x => x.IsBuildComplete(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task AnnotateAsync_MultipleEntries_CallsGetPendingBuildInfoOnce()
    {
        var entries = new[] { Entry("a.tif"), Entry("b.laz", EntryType.PointCloud) };
        _ddbMock.Setup(x => x.IsBuildable(It.IsAny<string>())).Returns(true);
        _ddbMock.Setup(x => x.IsBuildComplete(It.IsAny<string>())).Returns(true);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, entries);

        _ddbMock.Verify(x => x.GetPendingBuildInfo(), Times.Once);
        _jobIndexQueryMock.Verify(
            x => x.GetByOrgDsAsync(OrgSlug, DsSlug, 0, int.MaxValue, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
