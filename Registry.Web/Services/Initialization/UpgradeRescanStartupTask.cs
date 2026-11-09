using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Hosting;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Maintenance;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Storage;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// The upgrade rescans (§8.3, D-020, D-050): on the first start of a release that changes what an index holds, one
/// background job rescans the datasets, one at a time, through <see cref="IndexRescanService"/>. The entries it
/// rescans in a dataset are those of the types named by the registered <see cref="IUpgradeRescanStep"/>s not yet
/// done for it (the build-key rescan: models and splats; the vector footprint rescan of D-050 adds one step). It rebuilds only the entries whose build key
/// changed. The Registry serves requests while it runs: the job starts once the host has started
/// (<see cref="IBackgroundStartupTask"/>), reads and queues one dataset at a time, and the rebuilds run on the
/// background rebuild queue.
/// <para>
/// The progress is recorded per step in <see cref="MagicStrings.UpgradeRescansFileName"/> in the storage folder: the
/// datasets done for each step, so a dataset is rescanned once for a step, with the entry types of the steps not yet
/// done for it. A dataset that fails (the rescan throws, an entry cannot be read, a rebuild cannot be queued) is
/// logged, the job goes on with the others, and the next start tries it again. When every dataset is done for a step
/// the step is complete: a later start does not run it, and a dataset created after it is never rescanned for it. A
/// step a later release registers runs for every dataset.
/// </para>
/// <para>
/// A libddb that predates the build key reports no key change, so nothing would be rebuilt: the job records nothing
/// and stops, and runs at the first start with a libddb that has the key. Checked twice: on the capabilities of the
/// loaded library (ABI 2.<see cref="BuildKeyAbiMinor"/> or later) before any dataset is read, and on what its rescan
/// reports (<see cref="DatasetRescanOutcome.LibddbPredatesBuildKey"/>). One instance runs the job: it holds
/// <see cref="MagicStrings.UpgradeRescansLockFileName"/> open; another instance on the same storage skips it and tries
/// again at its next start.
/// </para>
/// </summary>
internal sealed class UpgradeRescanStartupTask : IBackgroundStartupTask
{
    /// <summary>The ABI minor of libddb 2.x that has the build key (DroneDB 2.2.0, U-210).</summary>
    internal const int BuildKeyAbiMinor = 2;

    private readonly RegistryContext _context;
    private readonly IndexRescanService _rescans;
    private readonly IEnumerable<IUpgradeRescanStep> _steps;
    private readonly IDdbManager _ddbManager;
    private readonly BuildScheduler _builds;
    private readonly IDdbRuntime _runtime;
    private readonly StorageDocument<UpgradeRescanState> _state;
    private readonly string _lockPath;
    private readonly ILogger<UpgradeRescanStartupTask> _logger;

    public UpgradeRescanStartupTask(RegistryContext context, IndexRescanService rescans,
        IEnumerable<IUpgradeRescanStep> steps, IDdbManager ddbManager, BuildScheduler builds, IDdbRuntime runtime,
        IOptions<StorageOptions> storage, IFileSystem fs, ILogger<UpgradeRescanStartupTask> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _rescans = rescans ?? throw new ArgumentNullException(nameof(rescans));
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _builds = builds ?? throw new ArgumentNullException(nameof(builds));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(storage);
        _state = new StorageDocument<UpgradeRescanState>(storage, fs, MagicStrings.UpgradeRescansFileName, logger);
        _lockPath = Path.Combine(storage.Value.StoragePath ?? ".", MagicStrings.UpgradeRescansLockFileName);
    }

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var steps = _steps.ToArray();
        if (steps.Length == 0)
            return;

        // The capabilities of the loaded library first: one that predates the build key is never asked to rescan
        var abiMinor = _runtime.Capabilities.AbiMinor;
        if (abiMinor < BuildKeyAbiMinor)
        {
            _logger.LogWarning("Upgrade rescans postponed: the loaded libddb has ABI {Major}.{Minor}, the build key " +
                               "needs ABI {Major}.{Required}. They run at the first start with a libddb that has it",
                _runtime.Capabilities.AbiMajor, abiMinor, _runtime.Capabilities.AbiMajor, BuildKeyAbiMinor);
            return;
        }

        using var lease = TryLock();
        if (lease == null)
            return;

        // Read under the lock: another instance may have recorded progress since
        var state = (_state.Read() ?? new UpgradeRescanState()).Normalize();
        var pendingSteps = steps.Where(s => !state.Step(s.Id).Completed).ToArray();
        if (pendingSteps.Length == 0)
            return;

        var filter = UpgradeRescanSteps.FilterOf(pendingSteps);
        var before = pendingSteps.ToDictionary(s => s.Id, s => state.Step(s.Id).Datasets.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var done = before.ToDictionary(p => p.Key, p => p.Value.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

        var datasets = await _context.Datasets.AsNoTracking()
            .OrderBy(d => d.Organization.Slug).ThenBy(d => d.Slug)
            .Select(d => new { d.Slug, OrgSlug = d.Organization.Slug, d.InternalRef })
            .ToArrayAsync(stoppingToken);
        var pending = datasets
            .Select(d => (Dataset: d, Steps: pendingSteps.Where(s => !done[s.Id].Contains(Key(d.InternalRef))).ToArray()))
            .Where(p => p.Steps.Length > 0)
            .ToArray();

        _logger.LogInformation("Upgrade rescans ({Steps}): {Pending} of {Total} dataset(s) to rescan ({Filter})",
            string.Join(", ", pendingSteps.Select(s => s.Id)), pending.Length, datasets.Length, filter);

        var failed = 0;
        foreach (var (dataset, datasetSteps) in pending)
        {
            stoppingToken.ThrowIfCancellationRequested();
            var name = $"{dataset.OrgSlug}/{dataset.Slug}";
            try
            {
                var target = new BuildTarget(_ddbManager.Get(dataset.OrgSlug, dataset.InternalRef),
                    new DatasetKey(dataset.OrgSlug, dataset.InternalRef), dataset.OrgSlug, dataset.Slug);
                var outcome = await _rescans.RescanDatasetNowAsync(dataset.OrgSlug, dataset.Slug, dataset.InternalRef,
                    UpgradeRescanSteps.FilterOf(datasetSteps), new BackgroundRebuilds(_builds, target), stoppingToken);

                if (outcome.LibddbPredatesBuildKey)
                {
                    // Nothing it rescanned can be told apart: recorded, the datasets would never be rebuilt
                    _logger.LogWarning("Upgrade rescans postponed: the loaded libddb predates the build key, so it " +
                                       "reports no build to move. They run at the first start with a libddb that has it");
                    // The datasets this run found without entries of the types are not recorded either: entries
                    // added before the libddb with the key arrives are rescanned then
                    if (done.Any(p => p.Value.Count != before[p.Key].Count))
                        Save(state, before, completed: false);
                    return;
                }

                if (outcome.Unreadable > 0 || outcome.RebuildFailures > 0)
                {
                    // Not recorded: the next start rescans it, and rebuilds what is still in the wrong folder
                    failed++;
                    _logger.LogWarning("Upgrade rescan of {Dataset} incomplete: {Unreadable} unreadable entries, " +
                                       "{RebuildFailures} rebuild(s) not queued; the next start tries it again",
                        name, outcome.Unreadable, outcome.RebuildFailures);
                    continue;
                }

                foreach (var step in datasetSteps)
                    done[step.Id].Add(Key(dataset.InternalRef));
                Save(state, done, completed: false);
                _logger.LogInformation("Upgrade rescan of {Dataset} done: {Rescanned} entries, {Rebuilt} rebuild(s) queued",
                    name, outcome.Rescanned, outcome.Rebuilt);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The others go on; the next start tries this one again
                failed++;
                _logger.LogError(ex, "Upgrade rescan of {Dataset} failed: {Error}", name, ex.Message);
            }
        }

        if (failed == 0)
        {
            Save(state, done, completed: true);
            _logger.LogInformation("Upgrade rescans complete");
        }
        else
            _logger.LogWarning("Upgrade rescans: {Failed} dataset(s) failed, the next start tries them again", failed);
    }

    /// <summary>
    /// Opens the lock file for this instance alone (an exclusive lock the OS releases if the process dies); null when
    /// another instance holds it, or it cannot be opened: the job is skipped and the next start tries again.
    /// </summary>
    private FileStream? TryLock()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_lockPath))!);
            return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            _logger.LogInformation("Upgrade rescans skipped: another instance runs them ({Path}: {Error}); " +
                                   "the next start tries again", _lockPath, ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Upgrade rescans skipped: cannot open {Path}; the next start tries again", _lockPath);
            return null;
        }
    }

    private void Save(UpgradeRescanState state, Dictionary<string, HashSet<string>> done, bool completed)
    {
        foreach (var (id, datasets) in done)
        {
            var step = state.Step(id);
            step.Datasets = [.. datasets.Order(StringComparer.Ordinal)];
            if (completed)
                step.Completed = true;
        }

        try
        {
            _state.Write(state);
        }
        catch (StorageDocumentException ex)
        {
            // Not recorded: the dataset is rescanned again at the next start, which is harmless
            _logger.LogWarning(ex, "Cannot record the upgrade rescans done");
        }
    }

    private static string Key(Guid internalRef) => internalRef.ToString("N");

    // The rebuilds of the entries whose key changed: nobody waits for them, so they run on the background rebuild queue
    private sealed class BackgroundRebuilds(BuildScheduler builds, BuildTarget target) : IRescanBuildRunner
    {
        public Task BuildDataset() =>
            throw new NotSupportedException("An upgrade rescan rebuilds entries, never the dataset");

        public Task BuildEntry(Registry.Ports.DroneDB.RescanResult entry) =>
            builds.QueueRebuildAsync(target, entry.Path, entry.Hash, BuildOrigin.Service);

        public Task BuildPending() => builds.RetryPendingAsync(target);
    }
}
