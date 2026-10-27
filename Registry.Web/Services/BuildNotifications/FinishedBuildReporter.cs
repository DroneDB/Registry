#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>
/// Turns the ends of builds into build notifications: a finished entry build job (<see cref="FinishedBuildJob"/>), judged
/// against libddb's view of the entry (<see cref="IBuildOutcomeClassifier"/>), and the pending builds whose automatic
/// retries ran out, which failed for good. Both go to <see cref="IBuildNotificationRecorder"/>.
/// </summary>
public sealed class FinishedBuildReporter
{
    private readonly IEmailService _email;
    private readonly DatasetSweep _datasets;
    private readonly IDdbManager _ddbManager;
    private readonly IBuildOutcomeClassifier _classifier;
    private readonly IBuildNotificationRecorder _recorder;
    private readonly BuildNotificationOutbox _outbox;
    private readonly ILogger<FinishedBuildReporter> _logger;

    public FinishedBuildReporter(IEmailService email, DatasetSweep datasets, IDdbManager ddbManager,
        IBuildOutcomeClassifier classifier, IBuildNotificationRecorder recorder, BuildNotificationOutbox outbox,
        ILogger<FinishedBuildReporter> logger)
    {
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Reports <paramref name="build"/> when its end is the final outcome of the entry's build.</summary>
    public async Task ReportAsync(FinishedBuildJob build, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(build);

        // The recorder would drop it anyway: spare the dataset lookup and the native calls
        if (!_email.IsEnabled)
            return;

        var ddb = await OpenAsync(build.OrgSlug, build.DsSlug).ConfigureAwait(false);
        if (ddb == null)
            return;

        var classified = _classifier.Classify(ddb, build.Path, build.Hash, build.Succeeded);
        if (classified == null)
        {
            _logger.LogDebug("Build job {JobId} of {Org}/{Ds}/{Path} did not end the build: no notification",
                build.JobId, build.OrgSlug, build.DsSlug, build.Path);
            return;
        }

        var error = classified.Outcome == BuildOutcome.Failed
            ? BuildReport.ShortError(JobLogText.Describe(build.Failure), ddb.DatasetFolderPath)
            : null;
        await _recorder.RecordAsync(new BuildReport(build.OrgSlug, build.DsSlug, build.Path, classified.Hash,
            classified.Outcome, error, new BuildRequest(build.UserId, build.JobId)), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The failed entry build <paramref name="jobId"/> runs again (a user's retry keeps the job id): its end answers a new
    /// request, so it is told even when it repeats the outcome already sent. Done whether e-mail is on or not, so lines
    /// kept from a time it was on never hide the end of the new run.
    /// </summary>
    public Task ReportRunAgainAsync(string jobId, CancellationToken ct) => _outbox.RenewRequestAsync(jobId, ct);

    /// <summary>
    /// Reports the pending builds of <paramref name="orgSlug"/>/<paramref name="dsSlug"/> whose automatic retries ran out
    /// as failed. They were retried as the build service: each goes to the user whose build of that content failed.
    /// </summary>
    public async Task ReportGivenUpAsync(string orgSlug, string dsSlug, IReadOnlyList<PendingBuildInfo> pending,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);

        if (!_email.IsEnabled)
            return;

        foreach (var entry in pending.Where(p => !string.IsNullOrWhiteSpace(p.Path) && !string.IsNullOrWhiteSpace(p.Hash)))
        {
            await _recorder.RecordAsync(new BuildReport(orgSlug, dsSlug, entry.Path, entry.Hash, BuildOutcome.Failed,
                    GivenUpError(entry), new BuildRequest(MagicStrings.AutoBuildServiceUserId, null)), ct)
                .ConfigureAwait(false);
        }
    }

    // libddb names the missing dependencies relative to the dataset: no server path in them
    private static string GivenUpError(PendingBuildInfo entry) =>
        BuildReport.ShortError(entry.MissingDependencies is { Length: > 0 } missing
            ? $"Not built after the automatic retries; missing: {string.Join(", ", missing)}"
            : "Not built after the automatic retries", null)!;

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
            // Deleted since: nobody is told about its builds
            return null;
        }
    }
}
