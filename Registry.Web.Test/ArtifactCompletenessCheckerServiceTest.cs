using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;
using DdbEntry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

[TestFixture]
public class ArtifactCompletenessCheckerServiceTest : TestBase
{
    private Mock<IDdbManager> _ddbManagerMock;
    private Mock<IBackgroundJobsProcessor> _backgroundJobMock;
    private ILogger<ArtifactCompletenessCheckerService> _logger;

    [SetUp]
    public void Setup()
    {
        _ddbManagerMock = new Mock<IDdbManager>();
        _backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        _logger = CreateTestLogger<ArtifactCompletenessCheckerService>();
    }

    [Test]
    public async Task CheckAndQueueAsync_EmptyDatabase_DoesNothing()
    {
        await using var context = GetEmptyContext();

        var service = new ArtifactCompletenessCheckerService(
            context, _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

        await service.CheckAndQueueAsync(null);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()),
            Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_NonBuildableEntries_NoRebuildEnqueued()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.Search(It.IsAny<string>(), true))
            .Returns(new[] { new DdbEntry { Path = "readme.txt", Hash = "h1" } });
        ddbMock.Setup(x => x.IsBuildable("readme.txt")).Returns(false);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(
            context, _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

        await service.CheckAndQueueAsync(null);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()),
            Times.Never);
        ddbMock.Verify(x => x.IsBuildComplete(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_BuildActive_Skipped()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.Search(It.IsAny<string>(), true))
            .Returns(new[] { new DdbEntry { Path = "ortho.tif", Hash = "h2" } });
        ddbMock.Setup(x => x.IsBuildable("ortho.tif")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("ortho.tif")).Returns(true);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(
            context, _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

        await service.CheckAndQueueAsync(null);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()),
            Times.Never);
        ddbMock.Verify(x => x.IsBuildComplete(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_BuildComplete_Skipped()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.Search(It.IsAny<string>(), true))
            .Returns(new[] { new DdbEntry { Path = "ortho.tif", Hash = "h2" } });
        ddbMock.Setup(x => x.IsBuildable("ortho.tif")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("ortho.tif")).Returns(false);
        ddbMock.Setup(x => x.IsBuildComplete("ortho.tif")).Returns(true);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(
            context, _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

        await service.CheckAndQueueAsync(null);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()),
            Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_BuildIncomplete_EnqueuesRebuild()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.Search(It.IsAny<string>(), true))
            .Returns(new[] { new DdbEntry { Path = "layer.geojson", Hash = "h3" } });
        ddbMock.Setup(x => x.IsBuildable("layer.geojson")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("layer.geojson")).Returns(false);
        ddbMock.Setup(x => x.IsBuildComplete("layer.geojson")).Returns(false);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(
            context, _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

        await service.CheckAndQueueAsync(null);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<Expression<Action>>(),
                It.Is<IndexPayload>(p =>
                    p.Path == "layer.geojson" &&
                    p.Hash == "h3" &&
                    p.UserId == MagicStrings.AutoBuildServiceUserId)),
            Times.Once);
    }

    [Test]
    public async Task CheckAndQueueAsync_MixedEntries_OnlyIncompleteEnqueued()
    {
        await using var context = GetTest1Context();

        var entries = new[]
        {
            new DdbEntry { Path = "readme.txt", Hash = "h1" },          // not buildable
            new DdbEntry { Path = "ortho.tif", Hash = "h2" },           // complete
            new DdbEntry { Path = "pc.laz",     Hash = "h3" },          // active
            new DdbEntry { Path = "layer.fgb",  Hash = "h4" },          // INCOMPLETE
        };
        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.Search(It.IsAny<string>(), true)).Returns(entries);
        ddbMock.Setup(x => x.IsBuildable("readme.txt")).Returns(false);
        ddbMock.Setup(x => x.IsBuildable("ortho.tif")).Returns(true);
        ddbMock.Setup(x => x.IsBuildable("pc.laz")).Returns(true);
        ddbMock.Setup(x => x.IsBuildable("layer.fgb")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("ortho.tif")).Returns(false);
        ddbMock.Setup(x => x.IsBuildActive("pc.laz")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("layer.fgb")).Returns(false);
        ddbMock.Setup(x => x.IsBuildComplete("ortho.tif")).Returns(true);
        ddbMock.Setup(x => x.IsBuildComplete("layer.fgb")).Returns(false);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(
            context, _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

        await service.CheckAndQueueAsync(null);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()),
            Times.Once);
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<Expression<Action>>(),
                It.Is<IndexPayload>(p => p.Path == "layer.fgb")),
            Times.Once);
    }

    [Test]
    public async Task CheckAndQueueAsync_DdbThrows_ContinuesWithOtherDatasets()
    {
        await using var context = GetTest1Context();

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Throws(new InvalidOperationException("boom"));

        var service = new ArtifactCompletenessCheckerService(
            context, _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

        // Should not throw, just log
        await service.CheckAndQueueAsync(null);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()),
            Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_EntryEvaluationThrows_SkipsButContinues()
    {
        await using var context = GetTest1Context();

        var entries = new[]
        {
            new DdbEntry { Path = "broken.tif", Hash = "h1" },
            new DdbEntry { Path = "good.tif",   Hash = "h2" },
        };
        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.Search(It.IsAny<string>(), true)).Returns(entries);
        ddbMock.Setup(x => x.IsBuildable("broken.tif"))
            .Throws(new InvalidOperationException("boom"));
        ddbMock.Setup(x => x.IsBuildable("good.tif")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("good.tif")).Returns(false);
        ddbMock.Setup(x => x.IsBuildComplete("good.tif")).Returns(false);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(
            context, _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

        await service.CheckAndQueueAsync(null);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<Expression<Action>>(),
                It.Is<IndexPayload>(p => p.Path == "good.tif")),
            Times.Once);
    }

    private static RegistryContext GetEmptyContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase("RegistryDatabase-" + Guid.NewGuid())
            .Options;
        return new RegistryContext(options);
    }

    private static RegistryContext GetTest1Context()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase("RegistryDatabase-" + Guid.NewGuid())
            .Options;

        using (var context = new RegistryContext(options))
        {
            var org = new Organization
            {
                Slug = MagicStrings.PublicOrganizationSlug,
                Name = "Public",
                CreationDate = DateTime.Now,
                Description = "Public organization",
                IsPublic = true,
                OwnerId = null
            };
            var ds = new Dataset
            {
                Slug = MagicStrings.DefaultDatasetSlug,
                CreationDate = DateTime.Now,
                InternalRef = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75")
            };
            org.Datasets = new List<Dataset> { ds };
            context.Organizations.Add(org);
            context.SaveChanges();
        }

        return new RegistryContext(options);
    }
}
