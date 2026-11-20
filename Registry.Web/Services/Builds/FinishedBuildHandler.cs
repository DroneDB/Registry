using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Builds;

/// <summary>
/// What the end of an entry's build job (<see cref="FinishedBuildJob"/>) calls for, from one reading of libddb's verdict
/// about the entry (<see cref="IBuildOutcomeClassifier"/>: built, failed for good, or waiting for files it names):
/// <list type="bullet">
/// <item>a build that waits for files keeps one task. libddb defers such a build with a normal return, so every attempt
/// (an upload of the same file, a forced build, a rescan) ends succeeded without building; the tasks of the earlier
/// attempts are removed (<see cref="IJobIndexWriter.DeleteEarlierSucceededBuildsAsync"/>) and the one that just ended is
/// the task the lists show waiting;</item>
/// <item>the outcome goes to the build notifications (<see cref="FinishedBuildReporter"/>).</item>
/// </list>
/// libddb is asked nothing when neither is due: no e-mail, and no earlier succeeded build of the entry.
/// </summary>
public sealed class FinishedBuildHandler
{
    private readonly DatasetSweep _datasets;
    private readonly IDdbManager _ddbManager;
    private readonly IBuildOutcomeClassifier _classifier;
    private readonly IJobIndexQuery _jobs;
    private readonly IJobIndexWriter _jobWriter;
    private readonly FinishedBuildReporter _reporter;
    private readonly ILogger<FinishedBuildHandler> _logger;

    public FinishedBuildHandler(DatasetSweep datasets, IDdbManager ddbManager, IBuildOutcomeClassifier classifier,
        IJobIndexQuery jobs, IJobIndexWriter jobWriter, FinishedBuildReporter reporter,
        ILogger<FinishedBuildHandler> logger)
    {
        _datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _jobWriter = jobWriter ?? throw new ArgumentNullException(nameof(jobWriter));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Handles the end of <paramref name="build"/> when it is the final outcome of the entry's build.</summary>
    public async Task EndedAsync(FinishedBuildJob build, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(build);

        // Nobody to tell and no earlier attempt to remove: spare the dataset lookup and the native calls
        if (!_reporter.IsEnabled && !await HasEarlierAttemptAsync(build, ct).ConfigureAwait(false))
            return;

        var ddb = await OpenAsync(build.OrgSlug, build.DsSlug).ConfigureAwait(false);
        if (ddb == null)
            return;

        var classified = _classifier.Classify(ddb, build.Path, build.Hash, build.Succeeded);
        if (classified == null)
        {
            _logger.LogDebug("Build job {JobId} of {Org}/{Ds}/{Path} did not end the build: nothing to tell",
                build.JobId, build.OrgSlug, build.DsSlug, build.Path);
            return;
        }

        if (classified.Outcome == BuildOutcome.WaitingForFiles)
        {
            var removed = await _jobWriter.DeleteEarlierSucceededBuildsAsync(build.OrgSlug, build.DsSlug, build.Path,
                classified.Hash, ct).ConfigureAwait(false);
            if (removed > 0)
                _logger.LogInformation(
                    "{Org}/{Ds}/{Path} still waits for files: removed the tasks of {Removed} earlier attempt(s)",
                    build.OrgSlug, build.DsSlug, build.Path, removed);
        }

        await _reporter.ReportAsync(build, classified, ddb.DatasetFolderPath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The failed entry build <paramref name="jobId"/> runs again (<see cref="FinishedBuildReporter.ReportRunAgainAsync"/>).
    /// </summary>
    public Task RunAgainAsync(string jobId, CancellationToken ct) => _reporter.ReportRunAgainAsync(jobId, ct);

    // A build of the entry succeeded before this one: only then can this end be one more attempt of a build that waits.
    // The row of this job already holds its final state (JobIndexStateFilter runs first), so it is one of the two
    private Task<bool> HasEarlierAttemptAsync(FinishedBuildJob build, CancellationToken ct) =>
        build.Succeeded
            ? _jobs.HasSeveralSucceededJobsAsync(build.OrgSlug, build.DsSlug, HeavyToolIds.Build, build.Path, ct)
            : Task.FromResult(false);

    private async Task<IDDB?> OpenAsync(string orgSlug, string dsSlug)
    {
        var dataset = await _datasets.FindAsync(orgSlug, dsSlug).ConfigureAwait(false);
        if (dataset?.Organization == null)
            return null;

        try
        {
            return _ddbManager.Open(orgSlug, dataset.InternalRef);
        }
        catch (DirectoryNotFoundException)
        {
            // Deleted since: its builds concern nobody
            return null;
        }
    }
}
