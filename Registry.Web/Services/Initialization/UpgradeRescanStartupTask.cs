using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
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
/// The loaded libddb has the build key and the WGS 84 vector footprints (the startup handshake requires the ABI of
/// DroneDB 2.6, <c>DdbAbi</c>), so every registered step runs. One instance runs the job: it holds the lock of
/// <see cref="UpgradeRescanProgress"/> open; another instance on the same storage skips it and tries again at its next
/// start.
/// </para>
/// </summary>
internal sealed class UpgradeRescanStartupTask : IBackgroundStartupTask
{
    private readonly RegistryContext _context;
    private readonly IndexRescanService _rescans;
    private readonly IEnumerable<IUpgradeRescanStep> _steps;
    private readonly IDdbManager _ddbManager;
    private readonly BuildScheduler _builds;
    private readonly UpgradeRescanProgress _progress;
    private readonly ILogger<UpgradeRescanStartupTask> _logger;

    public UpgradeRescanStartupTask(RegistryContext context, IndexRescanService rescans,
        IEnumerable<IUpgradeRescanStep> steps, IDdbManager ddbManager, BuildScheduler builds,
        UpgradeRescanProgress progress, ILogger<UpgradeRescanStartupTask> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _rescans = rescans ?? throw new ArgumentNullException(nameof(rescans));
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _builds = builds ?? throw new ArgumentNullException(nameof(builds));
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var steps = _steps.ToArray();
        if (steps.Length == 0)
            return;

        using var lease = _progress.TryLock();
        if (lease == null)
            return;

        // Read under the lock: another instance may have recorded progress since
        var state = _progress.Read();
        var pendingSteps = steps.Where(s => !state.Step(s.Id).Completed).ToArray();
        if (pendingSteps.Length == 0)
            return;

        var filter = UpgradeRescanSteps.FilterOf(pendingSteps);
        var done = pendingSteps.ToDictionary(s => s.Id, s => state.Step(s.Id).Datasets.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

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
            await BestEffort.RunAsync(async () =>
            {
                var target = new BuildTarget(_ddbManager.Get(dataset.OrgSlug, dataset.InternalRef),
                    new DatasetKey(dataset.OrgSlug, dataset.InternalRef), dataset.OrgSlug, dataset.Slug);
                var outcome = await _rescans.RescanDatasetNowAsync(dataset.OrgSlug, dataset.Slug, dataset.InternalRef,
                    UpgradeRescanSteps.FilterOf(datasetSteps), new BackgroundRebuilds(_builds, target), stoppingToken);

                if (outcome.SkippedBusy)
                {
                    // Not recorded: a task imports or rescans it now, and the next start rescans it
                    failed++;
                    _logger.LogInformation("Upgrade rescan of {Dataset} postponed: a task imports or rescans it now; " +
                                           "the next start tries it again", name);
                    return;
                }

                if (outcome.Unreadable > 0 || outcome.RebuildFailures > 0)
                {
                    // Not recorded: the next start rescans it, and rebuilds what is still in the wrong folder
                    failed++;
                    _logger.LogWarning("Upgrade rescan of {Dataset} incomplete: {Unreadable} unreadable entries, " +
                                       "{RebuildFailures} rebuild(s) not queued; the next start tries it again",
                        name, outcome.Unreadable, outcome.RebuildFailures);
                    return;
                }

                foreach (var step in datasetSteps)
                    done[step.Id].Add(Key(dataset.InternalRef));
                Save(state, done, completed: false);
                _logger.LogInformation("Upgrade rescan of {Dataset} done: {Rescanned} entries, {Rebuilt} rebuild(s) queued",
                    name, outcome.Rescanned, outcome.Rebuilt);
            }, ex =>
            {
                // The others go on; the next start tries this one again
                failed++;
                _logger.LogError(ex, "Upgrade rescan of {Dataset} failed: {Error}", name, ex.Message);
            }, stoppingToken);
        }

        if (failed == 0)
        {
            Save(state, done, completed: true);
            _logger.LogInformation("Upgrade rescans complete");
        }
        else
            _logger.LogWarning("Upgrade rescans: {Failed} dataset(s) failed, the next start tries them again", failed);
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

        _progress.Write(state);
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
