using System;
using System.IO;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// The dataset jobs (build, pending builds, cleanup, mask borders, thumbnail). A job names its
/// dataset by organization and <c>InternalRef</c> (never a DroneDB object, which Hangfire
/// would serialize) and opens it when it runs (<see cref="IDdbManager.Open"/>, which never
/// creates a dataset deleted in the meantime). Each job writes its progress through a
/// <see cref="JobConsole"/> scrubbed of the dataset folder (the build log tail is readable by
/// anonymous users on public datasets).
/// </summary>
/// <remarks>The retry policy is the class's (<see cref="DdbRetriedJob"/>); <see cref="Build"/> overrides it.</remarks>
public class DdbJobs : DdbRetriedJob
{
    private readonly IDdbManager _ddbManager;
    private readonly PendingBuildEnqueuer _pending;
    private readonly IFileSystem _fs;
    private readonly ILogger<DdbJobs> _logger;
    private readonly HostResourceBudget _budget;
    private readonly BuildJobProgress? _progress;

    /// <param name="progress">Writes the progress of a build job to its row (FR-PRC-42); null: the jobs report none.</param>
    public DdbJobs(IDdbManager ddbManager, PendingBuildEnqueuer pending, IFileSystem fs, ILogger<DdbJobs> logger,
        HostResourceBudget budget, BuildJobProgress? progress = null)
    {
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _pending = pending ?? throw new ArgumentNullException(nameof(pending));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _progress = progress;
    }

    /// <summary>Builds <paramref name="path"/> (null: the whole dataset).</summary>
    /// <remarks>
    /// The class policy without <see cref="DdbRetryableException"/>: a retryable build failure
    /// (low disk, tool idle timeout) fails the job, and the pending-build coordinator
    /// (<see cref="Adapters.BuildPendingService"/>) owns its retry with its own budgeted backoff;
    /// the artifact completeness check does not count it as a content failure. A second retry loop
    /// here would keep the job from failing.
    /// </remarks>
    [AutomaticRetry(Attempts = DdbRetryPolicy.Attempts,
        DelaysInSeconds = [DdbRetryPolicy.Delay1, DdbRetryPolicy.Delay2, DdbRetryPolicy.Delay3, DdbRetryPolicy.Delay4,
            DdbRetryPolicy.Delay5],
        OnAttemptsExceeded = AttemptsExceededAction.Fail,
        OnlyOn = [typeof(DdbBusyException), typeof(DdbBuildInProgressException)])]
    public void Build(string orgSlug, Guid internalRef, string? path, bool force, PerformContext? context) =>
        BuildJob(_ddbManager.Open(orgSlug, internalRef), path, force, context, null);

    /// <summary>
    /// Builds <paramref name="path"/> in the background: the build of an entry made with an older layout, which no
    /// user waits for. It runs on its own queue, one at a time, and uses at most
    /// <see cref="ProcessingBudget.BackgroundRebuildThreads"/> threads so the builds of users keep the host.
    /// </summary>
    /// <remarks>
    /// The retry policy and the failure handling of <see cref="Build"/>. An entry a user build completed meanwhile is
    /// already complete: libddb returns without building.
    /// </remarks>
    [AutomaticRetry(Attempts = DdbRetryPolicy.Attempts,
        DelaysInSeconds = [DdbRetryPolicy.Delay1, DdbRetryPolicy.Delay2, DdbRetryPolicy.Delay3, DdbRetryPolicy.Delay4,
            DdbRetryPolicy.Delay5],
        OnAttemptsExceeded = AttemptsExceededAction.Fail,
        OnlyOn = [typeof(DdbBusyException), typeof(DdbBuildInProgressException)])]
    public void RebuildInBackground(string orgSlug, Guid internalRef, string path, PerformContext? context) =>
        BuildJob(_ddbManager.Open(orgSlug, internalRef), path, false, context,
            BuildOptions.Capped(_budget.Processing?.BackgroundRebuildThreads ??
                                ProcessingBudget.DefaultBackgroundRebuildThreads));

    // A build job: the build, with its progress in the job's row when libddb reports it (FR-PRC-42)
    private void BuildJob(IDDB ddb, string? path, bool force, PerformContext? context, BuildOptions? options)
    {
        var sink = _progress?.Open(context);
        if (sink == null)
        {
            BuildNow(ddb, path, force, context, options);
            return;
        }

        try
        {
            BuildNow(ddb, path, force, context, options, sink);
        }
        finally
        {
            // The last percent and phase are written before the job's state changes
            sink.CompleteAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>Enqueues one build per due pending entry (<see cref="PendingBuildEnqueuer"/>).</summary>
    public void BuildPending(string orgSlug, Guid internalRef, PerformContext? context) =>
        BuildPendingNow(_ddbManager.Open(orgSlug, internalRef), context);

    /// <summary>Removes the entries whose file is gone and the orphaned build artifacts.</summary>
    public void Cleanup(string orgSlug, Guid internalRef, PerformContext? context)
    {
        var ddb = _ddbManager.Open(orgSlug, internalRef);
        var console = JobConsole.For(_logger, context, ddb.DatasetFolderPath);

        console.Debug("In Cleanup({Dataset})", ddb.DatasetFolderPath);

        var result = ddb.Cleanup();
        console.Info("Removed {Entries} entries and {Builds} build artifacts",
            result.Entries?.Length ?? 0, result.Builds?.Length ?? 0);
    }

    /// <summary>
    /// The mask-borders job stored before the <c>overwrite</c> parameter: its request had already removed the
    /// existing output, so any file there now came later from another writer and is refused, never replaced (D-040).
    /// </summary>
    public void MaskBorders(string orgSlug, Guid internalRef, string inputPath, string outputPath, int nearDist,
        bool white, PerformContext? context) =>
        MaskBorders(orgSlug, internalRef, inputPath, outputPath, nearDist, white, false, context);

    /// <summary>
    /// Masks the borders of <paramref name="inputPath"/> into <paramref name="outputPath"/>, indexes and builds it.
    /// <paramref name="overwrite"/> is set by a request that found the output and may replace it; without it, an
    /// output that exists when the job runs, or that another writer creates while it runs, is refused (D-040). An
    /// attempt after one that wrote the output (<see cref="PublishedOutputMarker"/>) only indexes and builds it.
    /// </summary>
    /// <exception cref="NameClashException">The output exists and <paramref name="overwrite"/> is false, or it did not
    /// exist when libddb started and another writer created it before the mask was written.</exception>
    public void MaskBorders(string orgSlug, Guid internalRef, string inputPath, string outputPath, int nearDist,
        bool white, bool overwrite, PerformContext? context)
    {
        var ddb = _ddbManager.Open(orgSlug, internalRef);
        var console = JobConsole.For(_logger, context, ddb.DatasetFolderPath);

        console.Debug("In MaskBorders({Dataset}, {Path})", ddb.DatasetFolderPath, inputPath);

        if (PublishedOutputMarker.Covers(ddb, PublishedOutputMarker.Get(context), outputPath))
            console.Debug("The masked file was written by an earlier attempt");
        else
        {
            if (!overwrite && DatasetEntryLookup.FindExisting(ddb, outputPath) is { } existing)
                throw new NameClashException(existing.Path);

            // libddb replaces an existing output only with overwrite, once the mask is complete: a failed or
            // cancelled job leaves it as it was (FR-PRC-01). The replace is decided now, for the file that is there:
            // an output that appears while libddb runs (another writer) is refused, never replaced
            var outputFullPath = ddb.GetLocalPath(outputPath);
            var replacing = overwrite && File.Exists(outputFullPath);
            console.Debug("Running mask borders");
            try
            {
                ddb.MaskBorders(inputPath, outputPath, nearDist, white, replacing);
            }
            catch (DdbException) when (!replacing && File.Exists(outputFullPath))
            {
                // Created by another writer since the check: kept, and refused as any existing output
                throw new NameClashException(outputPath);
            }

            // The very next step after the write: from here a later attempt indexes and builds the output.
            // NOTE: a process crash between the write and this line leaves the file without the marker
            PublishedOutputMarker.Set(context, outputPath);
        }

        console.Debug("Adding masked file to index");
        ddb.AddRaw(outputPath);

        console.Debug("Building COG for masked file");
        ddb.Build(outputPath);

        console.Debug("Done mask borders");
    }

    /// <summary>Writes a <paramref name="size"/> px thumbnail of <paramref name="path"/> to <paramref name="dest"/>.</summary>
    public void GenerateThumbnail(string orgSlug, Guid internalRef, string path, int size, string dest,
        PerformContext? context)
    {
        var ddb = _ddbManager.Open(orgSlug, internalRef);
        var console = JobConsole.For(_logger, context, ddb.DatasetFolderPath);

        console.Debug("In GenerateThumbnail({Dataset}, {Path}, {Size}, {Dest})", ddb.DatasetFolderPath, path, size, dest);

        console.Debug("Running generate thumbnail");
        var result = ddb.GenerateThumbnail(path, size);

        if (result != null)
        {
            console.Debug("Saving thumbnail");
            _fs.WriteAllBytes(dest, result);
        }
        else
        {
            console.Debug("Thumbnail generation failed");
        }

        console.Debug("Done generate thumbnail");
    }

    /// <summary>
    /// The build, run in the calling job (the build heavy tool and the rescan tool run it inline):
    /// a build whose lock another process holds is skipped (the holder does the work); any other
    /// failure is written to the job log tail and rethrown (Hangfire's Failed state, the retry
    /// policy, the failure filters). With <paramref name="progress"/>, the build reports its fraction and phase there
    /// (<see cref="BuildProgress"/>).
    /// </summary>
    public void BuildNow(IDDB ddb, string? path, bool force, PerformContext? context, BuildOptions? options = null,
        IProgress<HeavyTasks.Models.HeavyToolProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        var console = JobConsole.For(_logger, context, ddb.DatasetFolderPath);

        console.Debug("In Build({Dataset}, {Path}, {Force})", ddb.DatasetFolderPath, path, force);

        if (options?.Threads is { } threads)
            console.Debug("Running build on at most {Threads} thread(s)", threads);
        else
            console.Debug("Running build");
        var skipped = false;
        try
        {
            // null: the whole dataset
            if (progress != null)
                ddb.BuildWithProgress(path, force, options, BuildProgress.To(progress));
            else if (options == null)
                ddb.Build(path, force: force);
            else
                ddb.BuildWithOptions(path, options, force);
        }
        catch (DdbBuildInProgressException ex)
        {
            // Benign: the lock holder is doing the work. Throwing here would trip
            // AutomaticRetry -> Failed -> BuildJobFailureFilter and wrongly invalidate the cache.
            console.Debug("Build lock currently held by another process ({Reason}); skipping", ex.Message);
            skipped = true;
        }
        catch (OperationCanceledException)
        {
            // Hangfire canceled the job (shutdown: it is requeued; aborted: it ends here). libddb stopped the build
            // at its next checkpoint and left no partial artifact: not a build failure
            console.Debug("Build canceled");
            throw;
        }
        catch (Exception ex)
        {
            // Must land in the JobIndex log tail: without this the Task History shows only
            // "Running build" and admins cannot triage build failures at all. Rethrow keeps
            // Hangfire Failed-state semantics; the retry policy still fails non-transient errors fast.
            var error = JobLogText.Describe(ex);
            console.Debug("Build failed: {Error}", error);
            // The server log too: the job log is per task and BuildJobFailureFilter records only
            // the exception type
            _logger.LogWarning("Build job {JobId} failed for {Path}: {Error}", context?.BackgroundJob?.Id, path,
                JobLogText.ScrubDatasetPath(error, ddb.DatasetFolderPath));
            throw;
        }

        console.Debug(skipped ? "Done build (skipped: lock held elsewhere)" : "Done build");
    }

    /// <summary>The pending-build work, run in the calling job (see <see cref="BuildNow"/>).</summary>
    public void BuildPendingNow(IDDB ddb, PerformContext? context)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        _pending.Run(ddb, context, JobConsole.For(_logger, context, ddb.DatasetFolderPath));
    }
}
