using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Services.Builds;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="TaskFileIndexer"/>, the one copy of what the archive-extract, photogrammetry and import
/// tools did after writing files: index in chunks, ask libddb's build manifest once per chunk,
/// queue the builds of the buildable files as children of the task, invalidate the dataset's caches.
/// </summary>
[TestFixture]
public class TaskFileIndexerTests
{
    private const string Folder = "/datasets/org/ds";

    [Test]
    public async Task IndexAsync_IndexesInChunks_AsksTheManifestOncePerChunk_AndQueuesTheBuildableFiles()
    {
        var paths = Enumerable.Range(0, TaskFileIndexer.ChunkSize + 10).Select(i => $"f{i}.tif").ToArray();
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> chunk) =>
            chunk.Select(p => new Entry { Path = p, Hash = "h-" + p, Type = EntryType.GeoRaster }).ToArray());
        // Every 100th file is buildable
        ddb.Setup(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> chunk) =>
            chunk.Select(p => int.Parse(p[1..^4], System.Globalization.CultureInfo.InvariantCulture) % 100 == 0
                ? TestBuilds.Build(Folder, p, EntryType.GeoRaster, "h-" + p, BuildState.None)
                : TestBuilds.NotBuildable(p)).ToArray());
        var payloads = new List<IndexPayload>();
        var jobs = new Mock<IBackgroundJobsProcessor>();
        jobs.Setup(j => j.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()))
            .Callback<Expression<Action<DdbJobs>>, IndexPayload>((_, meta) => payloads.Add(meta))
            .Returns("job");
        var caches = new Mock<IDatasetCacheInvalidator>();
        var progress = new List<HeavyToolProgress>();

        var queued = await TestJobs.FileIndexer(caches.Object).IndexAsync(
            new TaskFiles(ddb.Object, new DatasetKey("org", Guid.NewGuid()), "org", "ds", paths, TestJobs.Scheduler(jobs.Object),
                "task-1", "extracted files", ProgressFrom: 0.5, ProgressTo: 0.7),
            new InlineProgress(progress.Add), CancellationToken.None);

        queued.ShouldBe(3);
        payloads.Select(p => p.Path).ShouldBe(["f0.tif", "f100.tif", "f200.tif"]);
        payloads.ShouldAllBe(p => p.ParentJobId == "task-1" && p.Hash == "h-" + p.Path && p.UserId == null);
        ddb.Verify(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>()), Times.Exactly(2));
        ddb.Verify(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()), Times.Exactly(2));
        progress.Select(p => p.Fraction).ShouldBe([0.5 + 0.2 * TaskFileIndexer.ChunkSize / paths.Length, 0.7]);
        caches.Verify(c => c.InvalidateAllDatasetCachesAsync("org", "ds"), Times.Once);
    }

    [Test]
    public async Task IndexAsync_WithoutFiles_StillInvalidates_AndQueuesNothing()
    {
        var caches = new Mock<IDatasetCacheInvalidator>();
        var jobs = new Mock<IBackgroundJobsProcessor>();

        var queued = await TestJobs.FileIndexer(caches.Object).IndexAsync(
            new TaskFiles(Mock.Of<IDDB>(), new DatasetKey("org", Guid.NewGuid()), "org", "ds", [], TestJobs.Scheduler(jobs.Object),
                "task-1", "imported files"), new InlineProgress(_ => { }), CancellationToken.None);

        queued.ShouldBe(0);
        jobs.VerifyNoOtherCalls();
        caches.Verify(c => c.InvalidateAllDatasetCachesAsync("org", "ds"), Times.Once);
    }

    /// <summary>
    /// The files a task wrote may be what pending builds waited for (an extracted .mtl): their arrival starts the
    /// pending-build retry once for the whole task, not once per chunk (red before: only the hourly sweep did).
    /// </summary>
    [Test]
    public async Task IndexAsync_StartsThePendingRetryOnce_AfterEveryChunkIsQueued()
    {
        var paths = Enumerable.Range(0, TaskFileIndexer.ChunkSize + 10).Select(i => $"f{i}.mtl").ToArray();
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> chunk) =>
            chunk.Select(p => new Entry { Path = p, Hash = "h-" + p, Type = EntryType.Generic }).ToArray());
        ddb.Setup(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> chunk) =>
            chunk.Select(p => TestBuilds.NotBuildable(p)).ToArray());
        var dataset = new DatasetKey("org", Guid.NewGuid());
        var retries = new Mock<IPendingBuildRetry>();

        await TestJobs.FileIndexer().IndexAsync(
            new TaskFiles(ddb.Object, dataset, "org", "ds", paths,
                TestJobs.Scheduler(Mock.Of<IBackgroundJobsProcessor>(), retries: retries.Object), "task-1", "extracted files"),
            new InlineProgress(_ => { }), CancellationToken.None);

        retries.Verify(r => r.StartIfRetryableAsync(It.Is<BuildTarget>(t =>
            t.Ddb == ddb.Object && t.Dataset == dataset && t.OrgSlug == "org" && t.DsSlug == "ds")), Times.Once);
    }

    private sealed class InlineProgress(Action<HeavyToolProgress> report) : IProgress<HeavyToolProgress>
    {
        public void Report(HeavyToolProgress value) => report(value);
    }
}
