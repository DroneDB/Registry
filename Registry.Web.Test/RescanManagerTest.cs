#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class RescanManagerTest : TestBase
{
    private Mock<IAuthManager> _authManagerMock = null!;
    private Mock<IDdbManager> _ddbManagerMock = null!;
    private Mock<IBackgroundJobsProcessor> _backgroundJobMock = null!;
    private Mock<IDatasetCacheInvalidator> _cacheInvalidatorMock = null!;

    [SetUp]
    public void Setup()
    {
        _authManagerMock = new Mock<IAuthManager>();
        _ddbManagerMock = new Mock<IDdbManager>();
        _backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        _cacheInvalidatorMock = new Mock<IDatasetCacheInvalidator>();
    }

    [Test]
    public async Task RescanDatasetIndex_NonAdmin_ThrowsUnauthorizedException()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);

        var rescanManager = CreateRescanManager(context);

        // Act
        Func<Task> act = async () => await rescanManager.RescanDatasetIndex("test-org", "test-dataset");

        // Assert
        var ex = await Should.ThrowAsync<UnauthorizedException>(act);
        ex.Message.ShouldContain("Only admins or users with write access can rescan dataset index");
    }

    [Test]
    public async Task RescanDatasetIndex_UserWithoutWriteAccess_ThrowsUnauthorizedException()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Dataset>(), AccessType.Write))
            .ReturnsAsync(false);

        var rescanManager = CreateRescanManager(context);

        // Act
        Func<Task> act = async () => await rescanManager.RescanDatasetIndex("test-org", "test-dataset");

        // Assert
        var ex = await Should.ThrowAsync<UnauthorizedException>(act);
        ex.Message.ShouldContain("write access");
    }

    [Test]
    public async Task RescanDatasetIndex_Admin_ValidDataset_ReturnsResults()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.RescanIndex(It.IsAny<RescanOptions?>()))
            .Returns([
                new RescanResult { Path = "image1.jpg", Success = true, Hash = "abc123" },
                new RescanResult { Path = "image2.jpg", Success = true, Hash = "def456" },
                new RescanResult { Path = "corrupted.jpg", Success = false, Error = "Invalid format" }
            ]);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var rescanManager = CreateRescanManager(context);

        // Act
        var result = await rescanManager.RescanDatasetIndex("test-org", "test-dataset");

        // Assert
        result.ShouldNotBeNull();
        result.OrganizationSlug.ShouldBe("test-org");
        result.DatasetSlug.ShouldBe("test-dataset");
        result.TotalProcessed.ShouldBe(3);
        result.SuccessCount.ShouldBe(2);
        result.ErrorCount.ShouldBe(1);
        result.Entries.Length.ShouldBe(3);
    }

    [Test]
    public async Task RescanDatasetIndex_Admin_WithTypesFilter_PassesTypesToDdb()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.RescanIndex(new RescanOptions("image,geoimage", null, true)))
            .Returns([new RescanResult { Path = "image1.jpg", Success = true, Hash = "abc123" }]);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var rescanManager = CreateRescanManager(context);

        // Act
        var result = await rescanManager.RescanDatasetIndex("test-org", "test-dataset", new RescanOptions("image,geoimage"));

        // Assert
        ddbMock.Verify(x => x.RescanIndex(new RescanOptions("image,geoimage", null, true)), Times.Once);
        result.ShouldNotBeNull();
    }

    [Test]
    public async Task RescanDatasetIndex_EmptyOrgSlug_ThrowsArgumentException()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var rescanManager = CreateRescanManager(context);

        // Act
        Func<Task> act = async () => await rescanManager.RescanDatasetIndex("", "test-dataset");

        // Assert
        var ex = await Should.ThrowAsync<ArgumentException>(act);
        ex.Message.ShouldContain("Organization slug");
    }

    [Test]
    public async Task RescanDatasetIndex_EmptyDsSlug_ThrowsArgumentException()
    {
        // Arrange
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var rescanManager = CreateRescanManager(context);

        // Act
        Func<Task> act = async () => await rescanManager.RescanDatasetIndex("test-org", "");

        // Assert
        var ex = await Should.ThrowAsync<ArgumentException>(act);
        ex.Message.ShouldContain("Dataset slug");
    }


    [Test]
    public async Task RescanDatasetIndex_NoFilter_ClearsBuildCacheAndRebuildsDataset()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.RescanIndex(new RescanOptions(null, null, true)))
            .Returns([new RescanResult { Path = "a.tif", Success = true, Hash = "h1" }]);
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);
        var payloads = CaptureIndexedPayloads();

        await CreateRescanManager(context).RescanDatasetIndex("test-org", "test-dataset");

        ddbMock.Verify(x => x.ClearBuildCache(), Times.Once);
        _cacheInvalidatorMock.Verify(x => x.InvalidateAllDatasetCachesAsync("test-org", "test-dataset"), Times.Once);
        // Whole-dataset build + pending build, no per-entry build
        payloads.Count.ShouldBe(2);
        payloads.ShouldAllBe(p => p.Path == null);
    }

    [Test]
    public async Task RescanDatasetIndex_ExtensionFilter_RebuildsOnlyRescannedBuildableEntries()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.RescanIndex(new RescanOptions(null, "dwg,dxf", true)))
            .Returns([
                new RescanResult { Path = "site.dwg", Success = true, Hash = "h1" },
                new RescanResult { Path = "notes.dwg", Success = true, Hash = "h2" },
                new RescanResult { Path = "broken.dxf", Success = false, Error = "bad file" }
            ]);
        ddbMock.Setup(x => x.GetBuildStatus(It.IsAny<string[]>())).Returns((string[] paths) => paths
            .Select(p => new EntryBuildStatus { Path = p, Exists = true, Buildable = p != "notes.dwg" }).ToArray());
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);
        var payloads = CaptureIndexedPayloads();

        var result = await CreateRescanManager(context)
            .RescanDatasetIndex("test-org", "test-dataset", new RescanOptions(Extensions: ".DWG, dxf"));

        result.TotalProcessed.ShouldBe(3);
        ddbMock.Verify(x => x.ClearBuildCache(), Times.Never);
        _cacheInvalidatorMock.Verify(x => x.InvalidateAllDatasetCachesAsync("test-org", "test-dataset"), Times.Once);
        // One forced build of site.dwg (with its hash) + the pending build
        payloads.Where(p => p.Path != null).Select(p => (p.Path, p.Hash)).ShouldBe([("site.dwg", "h1")]);
        payloads.Count(p => p.Path == null).ShouldBe(1);
    }

    [Test]
    public async Task RescanDatasetIndex_InvalidExtension_ThrowsArgumentException()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        await Should.ThrowAsync<ArgumentException>(() =>
            CreateRescanManager(context).RescanDatasetIndex("test-org", "test-dataset", new RescanOptions(Extensions: "*.dwg")));
    }

    [Test]
    public async Task Rescan_NonAdmin_ThrowsUnauthorizedException()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);

        await Should.ThrowAsync<UnauthorizedException>(() =>
            CreateRescanManager(context).Rescan(new RescanRequestDto { Extensions = ["dwg"] }));
    }

    [TestCase(null, null, null, null, TestName = "Rescan_NoFilter_ThrowsArgumentException")]
    [TestCase(null, "test-dataset", null, "dwg", TestName = "Rescan_DatasetWithoutOrganization_ThrowsArgumentException")]
    [TestCase("missing-org", null, null, "dwg", TestName = "Rescan_UnknownOrganization_ThrowsArgumentException")]
    [TestCase("test-org", "missing-dataset", null, "dwg", TestName = "Rescan_UnknownDataset_ThrowsArgumentException")]
    [TestCase(null, null, "nope", null, TestName = "Rescan_UnknownType_ThrowsArgumentException")]
    public async Task Rescan_InvalidRequest_ThrowsArgumentException(string? org, string? ds, string? type, string? ext)
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);

        await Should.ThrowAsync<ArgumentException>(() => CreateRescanManager(context).Rescan(new RescanRequestDto
        {
            OrganizationSlug = org,
            DatasetSlug = ds,
            Types = type == null ? null : [type],
            Extensions = ext == null ? null : [ext]
        }));
        _backgroundJobMock.Verify(
            x => x.Enqueue(It.IsAny<Expression<Func<IndexRescanService, Task>>>()), Times.Never);
    }

    [Test]
    public async Task Rescan_AllOrganizations_EnqueuesSubmitJobWithNormalizedFilter()
    {
        await using var context = CreateInMemoryContext();
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.GetCurrentUser())
            .ReturnsAsync(new Registry.Web.Identity.Models.User { Id = "admin-id" });

        Expression<Func<IndexRescanService, Task>>? job = null;
        _backgroundJobMock
            .Setup(x => x.Enqueue(It.IsAny<Expression<Func<IndexRescanService, Task>>>()))
            .Callback<Expression<Func<IndexRescanService, Task>>>(e => job = e)
            .Returns("job-1");

        var result = await CreateRescanManager(context).Rescan(new RescanRequestDto
        {
            Types = ["Vector"],
            Extensions = [".DWG", "dxf"]
        });

        result.JobId.ShouldBe("job-1");
        job.ShouldNotBeNull();
        var call = (MethodCallExpression)job.Body;
        call.Method.Name.ShouldBe(nameof(IndexRescanService.SubmitAsync));
        var args = call.Arguments.Select(a => Expression.Lambda(a).Compile().DynamicInvoke()).ToArray();
        args.ShouldBe(new object?[] { null, null, "vector", "dwg,dxf", false, "admin-id", null });
    }

    private List<IndexPayload> CaptureIndexedPayloads()
    {
        var payloads = new List<IndexPayload>();
        _backgroundJobMock
            .Setup(x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()))
            .Callback<Expression<Action>, IndexPayload>((_, p) => payloads.Add(p))
            .Returns("job");
        return payloads;
    }

    private RescanManager CreateRescanManager(RegistryContext context) =>
        new(_authManagerMock.Object, context, _ddbManagerMock.Object, _backgroundJobMock.Object,
            new RescanRebuild(_cacheInvalidatorMock.Object, CreateTestLogger<RescanRebuild>()),
            CreateTestLogger<RescanManager>());

    private static RegistryContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var context = new RegistryContext(options);

        var org = new Organization
        {
            Slug = "test-org",
            Name = "Test Organization",
            CreationDate = DateTime.UtcNow,
            OwnerId = "test-user"
        };
        context.Organizations.Add(org);
        context.Datasets.Add(new Dataset
        {
            Slug = "test-dataset",
            InternalRef = Guid.NewGuid(),
            CreationDate = DateTime.UtcNow,
            Organization = org
        });

        context.SaveChanges();
        return context;
    }
}
