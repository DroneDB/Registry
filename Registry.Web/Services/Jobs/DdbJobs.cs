#nullable enable
using System;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// The dataset jobs (build, pending builds, cleanup, mask borders, thumbnail). A job names its
/// dataset by organization and <c>InternalRef</c> (D4: never a DroneDB object, which Hangfire
/// would serialize) and opens it when it runs (<see cref="IDdbManager.Open"/>, which never
/// creates a dataset deleted in the meantime). Each job writes its progress through a
/// <see cref="JobConsole"/> scrubbed of the dataset folder (the build log tail is readable by
/// anonymous users on public datasets).
/// </summary>
/// <remarks>The retry policy is the class's (<see cref="DdbRetriedJob"/>); <see cref="Build"/> overrides it.</remarks>
public class DdbJobs : DdbRetriedJob, IDatasetBuilds
{
    private readonly IDdbManager _ddbManager;
    private readonly PendingBuildEnqueuer _pending;
    private readonly IFileSystem _fs;
    private readonly ILogger<DdbJobs> _logger;

    public DdbJobs(IDdbManager ddbManager, PendingBuildEnqueuer pending, IFileSystem fs, ILogger<DdbJobs> logger)
    {
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _pending = pending ?? throw new ArgumentNullException(nameof(pending));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Builds <paramref name="path"/> (null: the whole dataset).</summary>
    /// <remarks>
    /// The class policy without <see cref="DdbRetryableException"/> (XI-02): a retryable build failure
    /// (low disk, tool idle timeout) fails the job, and the pending-build coordinator
    /// (<see cref="Adapters.BuildPendingService"/>) owns its retry with its own budgeted backoff;
    /// the artifact completeness check does not count it as a content failure. A second retry loop
    /// here would keep the job from failing.
    /// </remarks>
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [15, 60, 180, 600, 1800],
        OnAttemptsExceeded = AttemptsExceededAction.Fail,
        OnlyOn = [typeof(DdbBusyException), typeof(DdbBuildInProgressException)])]
    public void Build(string orgSlug, Guid internalRef, string? path, bool force, PerformContext? context) =>
        BuildNow(_ddbManager.Open(orgSlug, internalRef), path, force, context);

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

    /// <summary>Masks the borders of <paramref name="inputPath"/> into <paramref name="outputPath"/>, indexes and builds it.</summary>
    public void MaskBorders(string orgSlug, Guid internalRef, string inputPath, string outputPath, int nearDist,
        bool white, PerformContext? context)
    {
        var ddb = _ddbManager.Open(orgSlug, internalRef);
        var console = JobConsole.For(_logger, context, ddb.DatasetFolderPath);

        console.Debug("In MaskBorders({Dataset}, {Path})", ddb.DatasetFolderPath, inputPath);

        console.Debug("Running mask borders");
        ddb.MaskBorders(inputPath, outputPath, nearDist, white);

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
    /// policy, the failure filters).
    /// </summary>
    public void BuildNow(IDDB ddb, string? path, bool force, PerformContext? context)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        var console = JobConsole.For(_logger, context, ddb.DatasetFolderPath);

        console.Debug("In Build({Dataset}, {Path}, {Force})", ddb.DatasetFolderPath, path, force);

        console.Debug("Running build");
        var skipped = false;
        try
        {
            ddb.Build(path!, force: force); // null: the whole dataset (the port predates annotations)
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
            // at its next checkpoint and left no partial artifact: not a build failure (RQ-13)
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
