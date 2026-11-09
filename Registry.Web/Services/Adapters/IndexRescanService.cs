using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Background job behind the system rescan endpoint: finds the datasets of a scope that
/// hold entries matching a <see cref="RescanFilter"/> and submits one <see cref="HeavyToolIds.RescanIndex"/>
/// task for each of them. The tasks run on the builds queue, so the heavy worker count
/// bounds how many datasets are rescanned at once, and each task shows in its dataset's
/// task list. Datasets without a matching entry get no task. The upgrade rescans of a release read one dataset
/// at a time, now, and rebuild only what changed (<see cref="RescanDatasetNowAsync"/>).
/// </summary>
public class IndexRescanService
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly ISystemTaskSubmitter _taskSubmitter;
    private readonly RescanRebuild _rebuild;
    private readonly ILogger<IndexRescanService> _logger;

    public IndexRescanService(
        RegistryContext context,
        IDdbManager ddbManager,
        ISystemTaskSubmitter taskSubmitter,
        RescanRebuild rebuild,
        ILogger<IndexRescanService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _taskSubmitter = taskSubmitter ?? throw new ArgumentNullException(nameof(taskSubmitter));
        _rebuild = rebuild ?? throw new ArgumentNullException(nameof(rebuild));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Rescans the entries of <paramref name="filter"/> in one dataset, now, and rebuilds only those whose build key
    /// changed (<see cref="RescanResult.BuildKeyChanged"/>, D-020): the rescan of an upgrade (<see cref="Initialization.UpgradeRescanStartupTask"/>).
    /// The rescan does not stop at an entry it cannot read: it is reported in the outcome and logged. A dataset with
    /// no matching entry is not opened for a rescan.
    /// </summary>
    /// <param name="builds">Queues the rebuilds (the entries of a rescan are rebuilt, not the dataset).</param>
    /// <exception cref="ArgumentException">The filter selects every entry (a rescan across datasets must not re-hash all data).</exception>
    public async Task<DatasetRescanOutcome> RescanDatasetNowAsync(string orgSlug, string dsSlug, Guid internalRef,
        RescanFilter filter, IRescanBuildRunner builds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(orgSlug);
        ArgumentException.ThrowIfNullOrEmpty(dsSlug);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(builds);
        filter.RequireNotEmpty();

        var ddb = _ddbManager.Get(orgSlug, internalRef);
        if (!ddb.FindSummaries(filter.ToEntryQuery()).Any(filter.Matches))
            return DatasetRescanOutcome.NothingToRescan;

        ct.ThrowIfCancellationRequested();
        // A native call that reads every matching file: not on the caller's thread of execution
        var results = await Task.Run(() => ddb.RescanIndex(filter.ToOptions(stopOnError: false)), CancellationToken.None);

        var unreadable = results.Where(r => !r.Success).ToArray();
        foreach (var entry in unreadable)
            _logger.LogWarning("Rescan of {Path} in {Org}/{Ds} failed: {Error}", entry.Path, orgSlug, dsSlug, entry.Error);

        var rebuilt = await _rebuild.RunAsync(orgSlug, dsSlug, ddb, filter, results, builds, ct: CancellationToken.None,
            onlyChangedKeys: true);

        _logger.LogInformation(
            "Rescanned {Count} entries ({Filter}) in {Org}/{Ds}: {Unreadable} unreadable, {Built} rebuild(s) queued, {Failed} failed",
            results.Count, filter, orgSlug, dsSlug, unreadable.Length, rebuilt.Built, rebuilt.Failed);
        return new DatasetRescanOutcome(results.Count, unreadable.Length, rebuilt.Built, rebuilt.Failed)
        {
            // A libddb with the build key names it in every entry it re-read (an empty key for a type without builds)
            LibddbPredatesBuildKey = results.Any(r => r.Success && r.BuildKey == null)
        };
    }

    /// <param name="orgSlug">Organization of the scope, or null for all organizations.</param>
    /// <param name="dsSlug">Dataset of the scope, or null for all datasets of the scope.</param>
    /// <param name="types">Comma-separated entry types, or null for all.</param>
    /// <param name="extensions">Comma-separated file extensions, or null for all.</param>
    /// <param name="stopOnError">Passed to each dataset rescan.</param>
    /// <param name="userId">The admin who asked for the rescan; the tasks are attributed to them.</param>
    /// <param name="context">Hangfire context, injected at run time.</param>
    [AutomaticRetry(Attempts = 0)]
    public async Task SubmitAsync(string? orgSlug, string? dsSlug, string? types, string? extensions,
        bool stopOnError, string? userId, PerformContext? context = null)
    {
        var console = JobConsole.For(_logger, context);

        var scope = DatasetScope.Parse(orgSlug, dsSlug);
        var filter = RescanFilter.Parse(types, extensions).RequireNotEmpty();

        var datasets = await scope.Apply(_context.Datasets.AsNoTracking())
            .OrderBy(d => d.Organization.Slug).ThenBy(d => d.Slug)
            .Select(d => new { d.Slug, OrgSlug = d.Organization.Slug, d.InternalRef })
            .ToArrayAsync();

        console.Info("Rescanning entries ({Filter}) in {Datasets} dataset(s) of {Scope}", filter, datasets.Length, scope);

        var prms = RescanIndexTool.CreateParams(filter, stopOnError);

        int submitted = 0, matched = 0, failed = 0;
        foreach (var ds in datasets)
        {
            var key = $"{ds.OrgSlug}/{ds.Slug}";
            try
            {
                var ddb = _ddbManager.Get(ds.OrgSlug, ds.InternalRef);
                var count = ddb.FindSummaries(filter.ToEntryQuery()).Count(filter.Matches);
                if (count == 0)
                    continue;

                var taskId = await _taskSubmitter.SubmitAsync(HeavyToolIds.RescanIndex, ds.OrgSlug, ds.Slug, ddb, null, prms, userId);
                submitted++;
                matched += count;
                console.Info("{Dataset}: {Count} matching entries, task {TaskId}", key, count, taskId);
            }
            catch (Exception ex)
            {
                failed++;
                console.Warning(ex, "cannot submit the rescan of {Dataset}: {Error}", key, ex.Message);
            }
        }

        console.Info("Rescan submitted: {Submitted} task(s) for {Matched} matching entries, " +
                     "{WithoutMatches} dataset(s) without matches, {Failed} failure(s)",
            submitted, matched, datasets.Length - submitted - failed, failed);
    }
}

/// <summary>What <see cref="IndexRescanService.RescanDatasetNowAsync"/> did.</summary>
/// <param name="Rescanned">The entries it rescanned.</param>
/// <param name="Unreadable">The entries it could not read.</param>
/// <param name="Rebuilt">The rebuilds it queued (the entries whose build key changed).</param>
/// <param name="RebuildFailures">The rebuilds it could not queue.</param>
public sealed record DatasetRescanOutcome(int Rescanned, int Unreadable, int Rebuilt, int RebuildFailures)
{
    /// <summary>
    /// The loaded libddb predates the build key (D-020): an entry it re-read has no <see cref="RescanResult.BuildKey"/>,
    /// so it reports no key change and nothing was rebuilt.
    /// </summary>
    public bool LibddbPredatesBuildKey { get; init; }

    /// <summary>The dataset has no entry of the filter's types: nothing was rescanned.</summary>
    public static DatasetRescanOutcome NothingToRescan { get; } = new(0, 0, 0, 0);
}
