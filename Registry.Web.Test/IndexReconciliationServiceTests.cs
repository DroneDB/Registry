using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

/// <summary>
/// Unit tests for <see cref="IndexReconciliationService"/>: unindexed-on-disk re-indexing,
/// missing-on-disk report-only behaviour,
/// and quarantine ageing.
/// </summary>
[TestFixture]
public class IndexReconciliationServiceTests
{
    private static RegistryContext CreateContext(out string orgSlug, out string dsSlug, out Guid internalRef)
    {
        orgSlug = "org";
        dsSlug = "ds";
        internalRef = Guid.NewGuid();

        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase("IndexReconciliationServiceTest_" + Guid.NewGuid())
            .Options;
        var context = new RegistryContext(options);

        context.Organizations.Add(new Organization
        {
            Slug = orgSlug, Name = orgSlug, CreationDate = DateTime.UtcNow, IsPublic = true
        });
        context.SaveChanges();
        var org = orgSlug;
        context.Datasets.Add(new Dataset
        {
            Slug = dsSlug, InternalRef = internalRef, CreationDate = DateTime.UtcNow,
            Organization = context.Organizations.First(o => o.Slug == org)
        });
        context.SaveChanges();

        return context;
    }

    private static IndexReconciliationService CreateService(RegistryContext context, string datasetFolderPath,
        Mock<IDDB> ddb, Mock<IDatasetIndexQueue> indexQueue, ReconciliationSettings? settings = null)
    {
        ddb.Setup(d => d.DatasetFolderPath).Returns(datasetFolderPath);

        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddb.Object);

        var appSettings = Microsoft.Extensions.Options.Options.Create(new AppSettings { Reconciliation = settings ?? new ReconciliationSettings() });

        return new IndexReconciliationService(context, ddbManager.Object, indexQueue.Object, appSettings,
            NullLogger<IndexReconciliationService>.Instance);
    }

    [Test]
    public async Task ReconcileAllDatasetsAsync_UnindexedFileOnDisk_IsReenqueuedThroughIndexQueue()
    {
        using var context = CreateContext(out var orgSlug, out _, out var internalRef);
        var root = Path.Combine(Path.GetTempPath(), "ddb_reconcile_test_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "unindexed.jpg"), "data");

            var ddb = new Mock<IDDB>();
            ddb.Setup(d => d.Search(".", true)).Returns(Enumerable.Empty<Entry>());

            var indexQueue = new Mock<IDatasetIndexQueue>();
            indexQueue.Setup(q => q.EnqueueAsync(It.IsAny<DatasetKey>(), It.IsAny<IReadOnlyList<string>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<Entry>)Array.Empty<Entry>());

            var service = CreateService(context, root, ddb, indexQueue);

            await service.ReconcileAllDatasetsAsync();

            indexQueue.Verify(q => q.EnqueueAsync(
                It.Is<DatasetKey>(k => k.OrgSlug == orgSlug && k.InternalRef == internalRef),
                It.Is<IReadOnlyList<string>>(paths => paths.Contains("unindexed.jpg")),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ReconcileAllDatasetsAsync_ReservedPaths_AreExcludedFromScan()
    {
        using var context = CreateContext(out _, out _, out _);
        var root = Path.Combine(Path.GetTempPath(), "ddb_reconcile_test_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, IDDB.DatabaseFolderName));
        Directory.CreateDirectory(Path.Combine(root, ".uploads"));
        try
        {
            File.WriteAllText(Path.Combine(root, IDDB.DatabaseFolderName, "dbase.sqlite"), "x");
            File.WriteAllText(Path.Combine(root, ".uploads", "in-flight.tmp"), "x");

            var ddb = new Mock<IDDB>();
            ddb.Setup(d => d.Search(".", true)).Returns(Enumerable.Empty<Entry>());

            var indexQueue = new Mock<IDatasetIndexQueue>();

            var service = CreateService(context, root, ddb, indexQueue);

            await service.ReconcileAllDatasetsAsync();

            // Nothing outside the reserved folders exists, so nothing should be re-enqueued
            indexQueue.Verify(q => q.EnqueueAsync(It.IsAny<DatasetKey>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Regression (review round 2): the walk used to be a single recursive
    /// <c>Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)</c>, which recursed
    /// into the live <c>.ddb</c> folder and could surface database internals as "unindexed".
    /// The iterative, pruned walk must never emit a path under a reserved prefix at any depth
    /// (not just depth-1 files like <c>.ddb/unrelated.bin</c>).
    /// </summary>
    [Test]
    public async Task ReconcileAllDatasetsAsync_NestedReservedDirs_ArePrunedDuringWalk()
    {
        using var context = CreateContext(out var orgSlug, out _, out var internalRef);
        var root = Path.Combine(Path.GetTempPath(), "ddb_reconcile_test_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, IDDB.DatabaseFolderName, "build"));
        Directory.CreateDirectory(Path.Combine(root, IDDB.UploadsFolderName, IDDB.QuarantineFolderName, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(root, IDDB.DatabaseFolderName, "db.sqlite"), "x");
            File.WriteAllText(Path.Combine(root, IDDB.DatabaseFolderName, "build", "widget.bin"), "x");
            File.WriteAllText(
                Path.Combine(root, IDDB.UploadsFolderName, IDDB.QuarantineFolderName, "nested", "stale.tif"), "x");
            File.WriteAllText(Path.Combine(root, "real.jpg"), "data");

            var ddb = new Mock<IDDB>();
            ddb.Setup(d => d.Search(".", true)).Returns(Enumerable.Empty<Entry>());

            var indexQueue = new Mock<IDatasetIndexQueue>();
            indexQueue.Setup(q => q.EnqueueAsync(It.IsAny<DatasetKey>(), It.IsAny<IReadOnlyList<string>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<Entry>)Array.Empty<Entry>());

            var service = CreateService(context, root, ddb, indexQueue);

            await service.ReconcileAllDatasetsAsync();

            // Exactly the one real file is re-enqueued; nested reserved-folder paths never leak.
            indexQueue.Verify(q => q.EnqueueAsync(
                It.Is<DatasetKey>(k => k.OrgSlug == orgSlug && k.InternalRef == internalRef),
                It.Is<IReadOnlyList<string>>(paths => paths.Count == 1 && paths[0] == "real.jpg"),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ReconcileAllDatasetsAsync_EverythingIndexed_DoesNotEnqueueAnything()
    {
        using var context = CreateContext(out _, out _, out _);
        var root = Path.Combine(Path.GetTempPath(), "ddb_reconcile_test_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "indexed.jpg"), "data");

            var ddb = new Mock<IDDB>();
            ddb.Setup(d => d.Search(".", true))
                .Returns([new Entry { Path = "indexed.jpg", Type = EntryType.Image }]);

            var indexQueue = new Mock<IDatasetIndexQueue>();

            var service = CreateService(context, root, ddb, indexQueue);

            await service.ReconcileAllDatasetsAsync();

            indexQueue.Verify(q => q.EnqueueAsync(It.IsAny<DatasetKey>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ReconcileAllDatasetsAsync_MissingOnDisk_IsReportedNotDeleted()
    {
        using var context = CreateContext(out _, out _, out _);
        var root = Path.Combine(Path.GetTempPath(), "ddb_reconcile_test_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            // Indexed entry has no corresponding file on disk.
            var ddb = new Mock<IDDB>();
            ddb.Setup(d => d.Search(".", true))
                .Returns([new Entry { Path = "gone.jpg", Type = EntryType.Image }]);

            var indexQueue = new Mock<IDatasetIndexQueue>();

            var service = CreateService(context, root, ddb, indexQueue);

            // Report-only: must not throw, must not touch the index queue or remove anything.
            await Should.NotThrowAsync(async () => await service.ReconcileAllDatasetsAsync());

            indexQueue.Verify(q => q.EnqueueAsync(It.IsAny<DatasetKey>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ReconcileAllDatasetsAsync_AgesOutOldQuarantineFilesButKeepsRecentOnes()
    {
        using var context = CreateContext(out _, out _, out _);
        var root = Path.Combine(Path.GetTempPath(), "ddb_reconcile_test_" + Guid.NewGuid());
        var quarantineDir = Path.Combine(root, ".uploads", "quarantine");
        Directory.CreateDirectory(quarantineDir);
        try
        {
            var oldFile = Path.Combine(quarantineDir, "old.jpg");
            var recentFile = Path.Combine(quarantineDir, "recent.jpg");
            File.WriteAllText(oldFile, "x");
            File.WriteAllText(recentFile, "x");
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
            File.SetLastWriteTimeUtc(recentFile, DateTime.UtcNow);

            var ddb = new Mock<IDDB>();
            ddb.Setup(d => d.Search(".", true)).Returns(Enumerable.Empty<Entry>());

            var indexQueue = new Mock<IDatasetIndexQueue>();

            var service = CreateService(context, root, ddb, indexQueue,
                new ReconciliationSettings { QuarantineRetentionDays = 7 });

            await service.ReconcileAllDatasetsAsync();

            File.Exists(oldFile).ShouldBeFalse();
            File.Exists(recentFile).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
