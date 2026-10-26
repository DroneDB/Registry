#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Hangfire.MySql;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// The pending-build job's work (<see cref="DdbJobs.BuildPending"/>): instead of letting libddb
/// run every due pending build inline (one opaque job for the whole dataset), it enqueues one
/// indexed <see cref="DdbJobs.Build"/> per due pending entry (a <see cref="DdbJobs.RebuildInBackground"/> for an
/// entry whose build still lists an artifact of an older layout), so a failure lands on that entry's
/// own job and the build status shows it as failed. It runs under the dataset's build
/// coordination lock (<see cref="DatasetBuildCoordinator"/>). Without the organization and
/// dataset of the job (job parameters set by <see cref="IndexedJobEnqueuer"/>), or a dataset
/// folder Registry cannot map, libddb runs the due builds inline (the native fallback).
/// An entry is due when the current time is at or past its <see cref="PendingBuildInfo.RetryAfter"/>:
/// libddb owns the retry rule, so the enqueue path and <c>buildPending</c> agree.
/// </summary>
public sealed class PendingBuildEnqueuer
{
    private readonly IBackgroundJobsProcessor _jobs;
    private readonly IJobIndexQuery _jobIndex;
    private readonly IDdbManager _ddbManager;
    private readonly JobStorage _storage;
    private readonly TimeProvider _time;
    private readonly string? _mysqlConnectionString;

    public PendingBuildEnqueuer(IBackgroundJobsProcessor jobs, IJobIndexQuery jobIndex, IDdbManager ddbManager,
        JobStorage storage, TimeProvider time, IConfiguration? configuration = null)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _jobIndex = jobIndex ?? throw new ArgumentNullException(nameof(jobIndex));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _mysqlConnectionString = storage is MySqlStorage
            ? configuration?.GetConnectionString(MagicStrings.HangfireConnectionName)
            : null;
    }

    /// <summary>
    /// The pending-build work of <paramref name="ddb"/> for the job running with
    /// <paramref name="context"/>: the organization and dataset come from its job parameters.
    /// </summary>
    public void Run(IDDB ddb, PerformContext? context, JobConsole console)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        ArgumentNullException.ThrowIfNull(console);

        console.Debug("In BuildPending({Dataset})", ddb.DatasetFolderPath);

        var (orgSlug, dsSlug, reason) = ReadDataset(context);
        if (reason == null && !_ddbManager.TryParseDatasetFolder(ddb.DatasetFolderPath, out _, out _))
            reason = "the dataset folder is not under the datasets path";

        if (reason != null)
        {
            console.Warning("{Reason}; falling back to native build pending", reason);
            RunNative(ddb, console);
            return;
        }

        Run(ddb, orgSlug!, dsSlug!, console, context?.Connection);
    }

    /// <summary>The enqueue path for <paramref name="orgSlug"/>/<paramref name="dsSlug"/>, under its coordination lock.</summary>
    internal void Run(IDDB ddb, string orgSlug, string dsSlug, JobConsole console, IStorageConnection? connection = null)
    {
        if (!_ddbManager.TryParseDatasetFolder(ddb.DatasetFolderPath, out var folderOrg, out var internalRef))
            throw new InvalidOperationException("The dataset folder is not under the datasets path");

        using (DatasetBuildCoordinator.Acquire(orgSlug, dsSlug, _storage, connection, _mysqlConnectionString))
            EnqueueDue(ddb, folderOrg, internalRef, orgSlug, dsSlug, console, connection);
    }

    private static (string? OrgSlug, string? DsSlug, string? Reason) ReadDataset(PerformContext? context)
    {
        if (context?.BackgroundJob?.Id == null || context.Connection == null)
            return (null, null, "no job context");

        try
        {
            // Raw read (as BuildRetrySchedulerFilter does): IndexedJobEnqueuer stores the slugs
            // as plain strings, not JSON, so PerformContext.GetJobParameter<T> would not parse them.
            var jobId = context.BackgroundJob.Id;
            var orgSlug = context.Connection.GetJobParameter(jobId, JobParamKeys.OrgSlug);
            var dsSlug = context.Connection.GetJobParameter(jobId, JobParamKeys.DsSlug);
            return string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(dsSlug)
                ? (null, null, "job has no organization/dataset parameters")
                : (orgSlug, dsSlug, null);
        }
        catch (Exception ex)
        {
            return (null, null, $"cannot read job parameters ({JobLogText.Describe(ex)})");
        }
    }

    private void EnqueueDue(IDDB ddb, string folderOrg, Guid internalRef, string orgSlug, string dsSlug,
        JobConsole console, IStorageConnection? connection)
    {
        console.Debug("Enqueueing due pending builds");

        try
        {
            var pending = ddb.GetPendingBuildInfo() ?? [];
            var now = _time.GetUtcNow().ToUnixTimeSeconds();

            var emptyPath = pending.Count(p => string.IsNullOrWhiteSpace(p.Path));
            var withPath = pending.Where(p => !string.IsNullOrWhiteSpace(p.Path)).ToArray();
            var due = withPath.Where(p => now >= p.RetryAfter).ToArray();
            var notDue = withPath.Length - due.Length;

            var activePaths = due.Length == 0
                ? new HashSet<string>()
                : ActiveBuildPaths(orgSlug, dsSlug, due.Select(p => p.Path).ToHashSet(), connection);

            var toEnqueue = new List<PendingBuildInfo>(due.Length);
            var active = 0;
            foreach (var entry in due)
            {
                // Add() is false when a job is already active, or is queued below for a
                // duplicate path.
                if (activePaths.Add(entry.Path))
                    toEnqueue.Add(entry);
                else
                    active++;
            }

            var inBackground = BackgroundRebuildPaths(ddb, toEnqueue, console);
            var dataset = new DatasetKey(folderOrg, internalRef);
            var enqueued = 0;
            foreach (var entry in toEnqueue)
            {
                var path = entry.Path;
                var meta = new IndexPayload(orgSlug, dsSlug, entry.Hash, MagicStrings.AutoBuildServiceUserId,
                    null, path);
                var background = inBackground.Contains(path);
                var jobId = background
                    ? _jobs.EnqueueRebuild(dataset, path, meta)
                    : _jobs.EnqueueBuild(dataset, path, false, meta);
                if (string.IsNullOrWhiteSpace(jobId))
                    throw new InvalidOperationException("Pending build enqueue returned no job ID");
                enqueued++;
                console.Debug(background
                    ? "Enqueued background rebuild of '{Path}' (job {JobId})"
                    : "Enqueued build of '{Path}' (job {JobId})", path, jobId);
            }

            console.Debug("Pending builds: {Pending}; enqueued {Enqueued}; skipped {Skipped} ({NotDue} not yet due, " +
                          "{Active} with an active build job, {EmptyPath} without a path)",
                pending.Length, enqueued, notDue + active + emptyPath, notDue, active, emptyPath);
        }
        catch (Exception ex)
        {
            console.Debug("Build pending failed: {Error}", JobLogText.Describe(ex));
            throw;
        }

        console.Debug("Done build pending");
    }

    // The entries whose build still lists an artifact of an older layout: the build that replaces it is a background
    // rebuild (the rebuilds queue, its thread cap), the rule the artifact completeness check queues it by, so the
    // retry of a failed background rebuild (it keeps its pending marker) does not come back as a user build on the
    // builds queue. Without the build manifest the entries are built on the builds queue, as before the rule.
    private static HashSet<string> BackgroundRebuildPaths(IDDB ddb, IReadOnlyList<PendingBuildInfo> entries,
        JobConsole console)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (entries.Count == 0)
            return paths;

        try
        {
            var builds = ddb.GetBuildArtifacts(entries.Select(e => e.Path).ToArray());
            if (builds == null || builds.Count != entries.Count)
            {
                console.Warning("The build manifest did not answer for {Count} pending entries; building them on " +
                                "the builds queue", entries.Count);
                return paths;
            }

            for (var i = 0; i < entries.Count; i++)
                if (builds[i].Build?.ListsLegacyArtifact() == true)
                    paths.Add(entries[i].Path);
        }
        catch (Exception ex)
        {
            console.Warning("Cannot read the build manifest of the pending entries ({Error}); building them on the " +
                            "builds queue", JobLogText.Describe(ex));
        }

        return paths;
    }

    // Hangfire is authoritative: a missing/terminal job cannot suppress a pending entry forever
    // through a stale Created/Indexing index row, while a live Indexing job (publication in progress
    // or awaiting recovery) is active and blocks a duplicate build.
    private HashSet<string> ActiveBuildPaths(string orgSlug, string dsSlug, HashSet<string> duePaths,
        IStorageConnection? connection)
    {
        var active = new HashSet<string>();
        var indexed = _jobIndex.GetByOrgDsAsync(orgSlug, dsSlug, 0, int.MaxValue).GetAwaiter().GetResult()
            .Where(j => j.ToolId == HeavyToolIds.Build && j.Path != null && duePaths.Contains(j.Path));

        using var owned = connection == null ? _storage.GetConnection() : null;
        var states = connection ?? owned!;
        foreach (var row in indexed)
        {
            var actualState = states.GetStateData(row.JobId)?.Name;
            if (actualState != null && TaskStateCatalog.Active.Contains(actualState))
                active.Add(row.Path!);
        }

        return active;
    }

    // The native fallback: libddb runs every due pending build inline in this job.
    private static void RunNative(IDDB ddb, JobConsole console)
    {
        console.Debug("Running build pending");
        var skipped = false;
        try
        {
            ddb.BuildPending();
        }
        catch (DdbBuildInProgressException ex)
        {
            // Benign: the lock holder will process the pending builds (see DdbJobs.Build)
            console.Debug("Build lock currently held by another process ({Reason}); skipping", ex.Message);
            skipped = true;
        }
        catch (Exception ex)
        {
            // Persist the failure reason into the JobIndex log tail, then rethrow so Hangfire marks
            // the job Failed (and retries apply per policy)
            console.Debug("Build pending failed: {Error}", JobLogText.Describe(ex));
            throw;
        }

        console.Debug(skipped ? "Done build pending (skipped: lock held elsewhere)" : "Done build pending");
    }
}
