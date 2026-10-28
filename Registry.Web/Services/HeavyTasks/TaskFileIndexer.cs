#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Builds;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// What a task does after it wrote files into a dataset (the sequence was copied in the
/// archive-extract, photogrammetry and dataset-import tools): index the files in chunks, queue a
/// build of every buildable one as a child job of the task, start the retry of the pending builds
/// that waited for one of them, invalidate the dataset's caches.
/// </summary>
/// <remarks>
/// Chunks bound the lock time of one index transaction and let progress be reported between them;
/// each chunk is one native index call, and buildability is one manifest call per chunk
/// (<see cref="BuildScheduler.QueueBuildableAsync"/>), not one call per file. Builds are queued once every
/// file is indexed, so they never run against a half-indexed dataset.
/// </remarks>
public sealed class TaskFileIndexer
{
    /// <summary>Files indexed (and asked about their build) per native call.</summary>
    public const int ChunkSize = 250;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TaskFileIndexer> _logger;

    /// <param name="scopes">Resolves the cache invalidator when it is used. The heavy tools that use this
    /// class are built inside Hangfire's configuration (the queue routing filter takes the tool registry,
    /// <c>InstallJobFilters</c>), so nothing they take at construction may need the job storage, which the
    /// invalidator does.</param>
    /// <param name="logger">The log.</param>
    public TaskFileIndexer(IServiceScopeFactory scopes, ILogger<TaskFileIndexer> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>
    /// Indexes <see cref="TaskFiles.Paths"/>, queues their builds and invalidates the dataset's caches.
    /// Progress goes from <see cref="TaskFiles.ProgressFrom"/> to <see cref="TaskFiles.ProgressTo"/>
    /// (indeterminate without files).
    /// </summary>
    /// <returns>The number of builds queued.</returns>
    public async Task<int> IndexAsync(TaskFiles files, IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);

        var paths = files.Paths;
        var indexed = new List<Entry>(paths.Count);
        for (var i = 0; i < paths.Count; i += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = paths.Skip(i).Take(ChunkSize).ToArray();
            indexed.AddRange(files.Ddb.AddRawBatch(chunk));
            var done = i + chunk.Length;
            progress.Report(new HeavyToolProgress(
                paths.Count > 0 ? files.ProgressFrom + (files.ProgressTo - files.ProgressFrom) * done / paths.Count : -1,
                "indexing", LogChunk: $"Indexed {done}/{paths.Count} file(s)"));
        }

        var queued = 0;
        var target = new BuildTarget(files.Ddb, files.Dataset, files.OrgSlug, files.DsSlug);
        var origin = new BuildOrigin(null, files.ParentJobId);
        foreach (var chunk in indexed.Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            queued += (await files.Builds.QueueBuildableAsync(target, [.. chunk.Select(e => e.Path)], origin, ct)).Count;
        }

        // Once for all the files (not per chunk): the builds that waited for one of them are due now
        if (indexed.Count > 0)
            await files.Builds.RetryPendingAsync(target);

        _logger.LogInformation("Enqueued {Count} build job(s) for {What} in {Org}/{Ds}",
            queued, files.What, files.OrgSlug, files.DsSlug);

        // Cached tiles, thumbnails and OGC documents (keyed by organization and dataset, no user)
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDatasetCacheInvalidator>()
                .InvalidateAllDatasetCachesAsync(files.OrgSlug, files.DsSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache invalidation after indexing {What} failed for {Org}/{Ds}",
                files.What, files.OrgSlug, files.DsSlug);
        }

        return queued;
    }
}

/// <summary>Files a task wrote into a dataset, for <see cref="TaskFileIndexer"/>.</summary>
/// <param name="Ddb">The dataset's index.</param>
/// <param name="Dataset">The dataset, as a background job names it.</param>
/// <param name="OrgSlug">Organization slug (cache keys, job payloads).</param>
/// <param name="DsSlug">Dataset slug (cache keys, job payloads).</param>
/// <param name="Paths">Dataset paths of the files, normalized.</param>
/// <param name="Builds">Queues the builds, in the task's scope.</param>
/// <param name="ParentJobId">The task's job: the builds are its children.</param>
/// <param name="What">What the files are, for the log ("extracted files").</param>
/// <param name="ProgressFrom">Task progress when indexing starts.</param>
/// <param name="ProgressTo">Task progress when the last file is indexed.</param>
public sealed record TaskFiles(
    IDDB Ddb, DatasetKey Dataset, string OrgSlug, string DsSlug, IReadOnlyList<string> Paths,
    BuildScheduler Builds, string ParentJobId, string What,
    double ProgressFrom = 0, double ProgressTo = 1);
