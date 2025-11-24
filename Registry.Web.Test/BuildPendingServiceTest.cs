using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
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

namespace Registry.Web.Test;

[TestFixture]
public class BuildPendingServiceTest : TestBase
{
    private Mock<IDdbManager> _ddbManagerMock;
    private Mock<IBackgroundJobsProcessor> _backgroundJobMock;
    private Mock<ICacheManager> _cacheManager;
    private ILogger<BuildPendingService> _logger;

    [SetUp]
    public void Setup()
    {
        _ddbManagerMock = new Mock<IDdbManager>();
        _backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        _cacheManager = new Mock<ICacheManager>();
        _logger = CreateTestLogger<BuildPendingService>();
    }

    [Test]
    public async Task ProcessPendingBuilds_EmptyDatabase_CompletesSuccessfully()
    {
        // Arrange
        await using var context = GetEmptyContext();

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should complete without errors
        _backgroundJobMock.Verify(x => x.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action>>(), It.IsAny<IndexPayload>()), Times.Never);
    }

    [Test]
    public async Task ProcessPendingBuilds_DatasetWithPending_EnqueuesJob()
    {
        // Arrange
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.IsBuildPending()).Returns(true);
        ddbMock.Setup(x => x.GetStamp()).Returns(new Stamp
        {
            Checksum = "test-checksum-123",
            Entries = [],
            Meta = []
        });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        _cacheManager.Setup(x => x.IsRegistered(It.IsAny<string>())).Returns(false);
        _cacheManager.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((byte[])null);

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should enqueue at least one job
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<System.Linq.Expressions.Expression<Action>>(),
                It.IsAny<IndexPayload>()
            ),
            Times.AtLeastOnce
        );
    }

    [Test]
    public async Task ProcessPendingBuilds_DatasetNoPending_SkipsEnqueue()
    {
        // Arrange
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.IsBuildPending()).Returns(false);
        ddbMock.Setup(x => x.GetStamp()).Returns(new Stamp
        {
            Checksum = "test-checksum-456",
            Entries = [],
            Meta = new List<string>()
        });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        _cacheManager.Setup(x => x.IsRegistered(It.IsAny<string>())).Returns(false);
        _cacheManager.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((byte[])null);

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should NOT enqueue any jobs
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<System.Linq.Expressions.Expression<Action>>(),
                It.IsAny<IndexPayload>()
            ),
            Times.Never
        );
    }

    [Test]
    public async Task ProcessPendingBuilds_CacheHasPending_AlwaysChecks()
    {
        // Arrange
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.IsBuildPending()).Returns(true);
        ddbMock.Setup(x => x.GetStamp()).Returns(new Stamp
        {
            Checksum = "test-checksum-789",
            Entries = [],
            Meta = new List<string>()
        });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        // Cache state with HasPending=true
        var cacheState = System.Text.Json.JsonSerializer.Serialize(new
        {
            HasPending = true,
            LastCheckBinary = DateTime.UtcNow.ToBinary(),
            StampChecksum = "old-checksum"
        });
        var cacheBytes = System.Text.Encoding.UTF8.GetBytes(cacheState);

        _cacheManager.Setup(x => x.IsRegistered(It.IsAny<string>())).Returns(true);
        _cacheManager.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(cacheBytes);

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should check DDB and enqueue
        ddbMock.Verify(x => x.IsBuildPending(), Times.AtLeastOnce);
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<System.Linq.Expressions.Expression<Action>>(),
                It.IsAny<IndexPayload>()
            ),
            Times.AtLeastOnce
        );
    }

    [Test]
    public async Task ProcessPendingBuilds_CacheNoPendingRecent_SkipsCheck()
    {
        // Arrange
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.IsBuildPending()).Returns(false);
        ddbMock.Setup(x => x.GetStamp()).Returns(new Stamp
        {
            Checksum = "same-checksum",
            Entries = [],
            Meta = []
        });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        // Cache state with HasPending=false and recent check (2 hours ago) with SAME checksum
        var cacheState = System.Text.Json.JsonSerializer.Serialize(new
        {
            HasPending = false,
            LastCheckBinary = DateTime.UtcNow.AddHours(-2).ToBinary(),
            StampChecksum = "same-checksum"
        });
        var cacheBytes = System.Text.Encoding.UTF8.GetBytes(cacheState);

        _cacheManager.Setup(x => x.IsRegistered(It.IsAny<string>())).Returns(true);
        _cacheManager.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(cacheBytes);

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should NOT check DDB (cache optimization)
        ddbMock.Verify(x => x.IsBuildPending(), Times.Never);
    }

    [Test]
    public async Task ProcessPendingBuilds_CacheNoPendingStale_ForcesCheck()
    {
        // Arrange
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.IsBuildPending()).Returns(false);
        ddbMock.Setup(x => x.GetStamp()).Returns(new Stamp
        {
            Checksum = "new-stale-checksum",
            Entries = [],
            Meta = new List<string>()
        });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        // Cache state with HasPending=false and stale check (7 hours ago - exceeds 6 hour threshold)
        // with a DIFFERENT checksum to force the check
        var cacheState = System.Text.Json.JsonSerializer.Serialize(new
        {
            HasPending = false,
            LastCheckBinary = DateTime.UtcNow.AddHours(-7).ToBinary(),
            StampChecksum = "old-stale-checksum"
        });
        var cacheBytes = System.Text.Encoding.UTF8.GetBytes(cacheState);

        _cacheManager.Setup(x => x.IsRegistered(It.IsAny<string>())).Returns(true);
        _cacheManager.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(cacheBytes);

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should check DDB due to checksum change
        ddbMock.Verify(x => x.IsBuildPending(), Times.AtLeastOnce);
    }

    [Test]
    public async Task ProcessPendingBuilds_ClockSkew_ForcesCheck()
    {
        // Arrange
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.IsBuildPending()).Returns(false);
        ddbMock.Setup(x => x.GetStamp()).Returns(new Stamp
        {
            Checksum = "new-clockskew-checksum",
            Entries = [],
            Meta = []
        });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        // Cache state with LastCheck in the FUTURE (clock skew scenario)
        // with a DIFFERENT checksum to force the check
        var cacheState = System.Text.Json.JsonSerializer.Serialize(new
        {
            HasPending = false,
            LastCheckBinary = DateTime.UtcNow.AddHours(1).ToBinary(),
            StampChecksum = "old-clockskew-checksum"
        });
        var cacheBytes = System.Text.Encoding.UTF8.GetBytes(cacheState);

        _cacheManager.Setup(x => x.IsRegistered(It.IsAny<string>())).Returns(true);
        _cacheManager.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(cacheBytes);

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should check DDB due to checksum change
        ddbMock.Verify(x => x.IsBuildPending(), Times.AtLeastOnce);
    }

    [Test]
    public async Task ProcessPendingBuilds_DdbAccessError_ContinuesWithOtherDatasets()
    {
        // Arrange
        await using var context = GetTest1Context();

        // Add second dataset
        var org = context.Organizations.First();
        var dataset2 = new Dataset
        {
            Slug = "test-dataset-2",
            InternalRef = Guid.NewGuid(),
            CreationDate = DateTime.Now,
            Organization = org
        };
        context.Datasets.Add(dataset2);
        await context.SaveChangesAsync();

        var ddbMock1 = new Mock<IDDB>();
        ddbMock1.Setup(x => x.IsBuildPending()).Returns(true);

        var ddbMock2 = new Mock<IDDB>();
        ddbMock2.Setup(x => x.IsBuildPending()).Returns(true);
        ddbMock2.Setup(x => x.GetStamp()).Returns(new Stamp
        {
            Checksum = "error-recovery-checksum",
            Entries = [],
            Meta = []
        });

        // First dataset throws error, second succeeds
        _ddbManagerMock.SetupSequence(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Throws(new Exception("DDB access failed"))
            .Returns(ddbMock2.Object);

        _cacheManager.Setup(x => x.IsRegistered(It.IsAny<string>())).Returns(false);
        _cacheManager.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((byte[])null);

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should still process second dataset despite first failing
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<System.Linq.Expressions.Expression<Action>>(),
                It.IsAny<IndexPayload>()
            ),
            Times.AtLeastOnce
        );
    }

    [Test]
    public async Task ProcessPendingBuilds_UpdatesCache_AfterCheck()
    {
        // Arrange
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.IsBuildPending()).Returns(false);
        ddbMock.Setup(x => x.GetStamp()).Returns(new Stamp
        {
            Checksum = "cache-update-checksum",
            Entries = [],
            Meta = []
        });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        _cacheManager.Setup(x => x.IsRegistered(It.IsAny<string>())).Returns(false);
        _cacheManager.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((byte[])null);

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should update cache with results
        _cacheManager.Verify(
            x => x.SetAsync(
                MagicStrings.BuildPendingTrackerCacheSeed,
                It.IsAny<string>(),
                It.IsAny<byte[]>()
            ),
            Times.AtLeastOnce
        );
    }

    [Test]
    public async Task ProcessPendingBuilds_ChecksumChanged_ForcesCheck()
    {
        // Arrange
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.IsBuildPending()).Returns(false);
        ddbMock.Setup(x => x.GetStamp()).Returns(new Stamp
        {
            Checksum = "new-checksum-abc",
            Entries = [],
            Meta = []
        });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        // Cache state with HasPending=false and DIFFERENT checksum
        var cacheState = System.Text.Json.JsonSerializer.Serialize(new
        {
            HasPending = false,
            LastCheckBinary = DateTime.UtcNow.AddHours(-1).ToBinary(),
            StampChecksum = "old-checksum-xyz"
        });
        var cacheBytes = System.Text.Encoding.UTF8.GetBytes(cacheState);

        _cacheManager.Setup(x => x.IsRegistered(It.IsAny<string>())).Returns(true);
        _cacheManager.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(cacheBytes);

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should check DDB because checksum changed
        ddbMock.Verify(x => x.IsBuildPending(), Times.AtLeastOnce);
    }

    [Test]
    public async Task ProcessPendingBuilds_PendingButStampUnchanged_SkipsEnqueue()
    {
        // Arrange
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.IsBuildPending()).Returns(true);
        ddbMock.Setup(x => x.GetStamp()).Returns(new Stamp
        {
            Checksum = "unchanged-checksum-123",
            Entries = [],
            Meta = []
        });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        // Cache state with HasPending=true and SAME checksum
        // This means the same files are still pending from before
        var cacheState = System.Text.Json.JsonSerializer.Serialize(new
        {
            HasPending = true,
            LastCheckBinary = DateTime.UtcNow.AddHours(-1).ToBinary(),
            StampChecksum = "unchanged-checksum-123"
        });
        var cacheBytes = System.Text.Encoding.UTF8.GetBytes(cacheState);

        _cacheManager.Setup(x => x.IsRegistered(It.IsAny<string>())).Returns(true);
        _cacheManager.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(cacheBytes);

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should check pending status but NOT enqueue job (stamp unchanged)
        ddbMock.Verify(x => x.IsBuildPending(), Times.AtLeastOnce);
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<System.Linq.Expressions.Expression<Action>>(),
                It.IsAny<IndexPayload>()
            ),
            Times.Never
        );
    }

    [Test]
    public async Task ProcessPendingBuilds_PendingAndStampChanged_EnqueuesJob()
    {
        // Arrange
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.IsBuildPending()).Returns(true);
        ddbMock.Setup(x => x.GetStamp()).Returns(new Stamp
        {
            Checksum = "new-checksum-456",
            Entries = [],
            Meta = []
        });

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        // Cache state with HasPending=true but DIFFERENT checksum
        // This means new files were added to pending
        var cacheState = System.Text.Json.JsonSerializer.Serialize(new
        {
            HasPending = true,
            LastCheckBinary = DateTime.UtcNow.AddHours(-1).ToBinary(),
            StampChecksum = "old-checksum-123"
        });
        var cacheBytes = System.Text.Encoding.UTF8.GetBytes(cacheState);

        _cacheManager.Setup(x => x.IsRegistered(It.IsAny<string>())).Returns(true);
        _cacheManager.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(cacheBytes);

        var service = new BuildPendingService(
            context,
            _ddbManagerMock.Object,
            _backgroundJobMock.Object,
            _cacheManager.Object,
            _logger
        );

        // Act
        await service.ProcessPendingBuilds(null);

        // Assert - Should check pending status AND enqueue job (stamp changed)
        ddbMock.Verify(x => x.IsBuildPending(), Times.AtLeastOnce);
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<System.Linq.Expressions.Expression<Action>>(),
                It.IsAny<IndexPayload>()
            ),
            Times.AtLeastOnce
        );
    }

    private RegistryContext GetEmptyContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: "EmptyTestDb_" + Guid.NewGuid())
            .Options;

        var context = new RegistryContext(options);
        return context;
    }

    private static RegistryContext GetTest1Context()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: "RegistryDatabase-" + Guid.NewGuid())
            .Options;

        using (var context = new RegistryContext(options))
        {
            var entity = new Organization
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

            entity.Datasets = new List<Dataset> { ds };

            context.Organizations.Add(entity);

            context.SaveChanges();
        }

        return new RegistryContext(options);
    }
}
