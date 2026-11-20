using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>
/// Turns the ends of builds into build notifications: a finished entry build job (<see cref="FinishedBuildJob"/>) with
/// libddb's verdict about its entry (<see cref="ClassifiedBuild"/>: built, failed for good, or waiting for files it
/// names), and the pending builds whose automatic retries ran out, which failed for good. Both go to <see cref="IBuildNotificationRecorder"/>.
/// </summary>
public sealed class FinishedBuildReporter
{
    private readonly IEmailService _email;
    private readonly IBuildNotificationRecorder _recorder;
    private readonly BuildNotificationOutbox _outbox;

    public FinishedBuildReporter(IEmailService email, IBuildNotificationRecorder recorder, BuildNotificationOutbox outbox)
    {
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    }

    /// <summary>Whether an end is reported at all: without e-mail the recorder would drop it.</summary>
    public bool IsEnabled => _email.IsEnabled;

    /// <summary>
    /// Reports <paramref name="build"/>, whose end is the final outcome <paramref name="classified"/> of the entry's
    /// build. <paramref name="datasetFolder"/> is the folder of the dataset, which the message of a failure never shows.
    /// </summary>
    public async Task ReportAsync(FinishedBuildJob build, ClassifiedBuild classified, string datasetFolder,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(classified);

        if (!IsEnabled)
            return;

        var error = classified.Outcome switch
        {
            BuildOutcome.Failed => BuildReport.ShortError(JobLogText.UserMessage(build.Failure, datasetFolder),
                datasetFolder),
            // libddb names the missing files relative to the dataset
            BuildOutcome.WaitingForFiles => BuildReport.ShortError(string.Join(", ", classified.MissingFiles),
                datasetFolder),
            _ => null
        };
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
    /// as failed. They were retried as the build service: each goes to the user whose build of that content failed, or
    /// found files missing before the files arrived and released it (<see cref="IBuildRequesterResolver"/>).
    /// </summary>
    public async Task ReportGivenUpAsync(string orgSlug, string dsSlug, IReadOnlyList<PendingBuildInfo> pending,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);

        if (!IsEnabled)
            return;

        foreach (var entry in pending.Where(p => !string.IsNullOrWhiteSpace(p.Path) && !string.IsNullOrWhiteSpace(p.Hash)))
        {
            await _recorder.RecordAsync(new BuildReport(orgSlug, dsSlug, entry.Path, entry.Hash, BuildOutcome.Failed,
                    GivenUpError(entry), BuildRequest.RetriesGivenUp), ct)
                .ConfigureAwait(false);
        }
    }

    // libddb names the missing dependencies relative to the dataset: no server path in them
    private static string GivenUpError(PendingBuildInfo entry) =>
        BuildReport.ShortError(entry.MissingDependencies is { Length: > 0 } missing
            ? $"Not built after the automatic retries; missing: {string.Join(", ", missing)}"
            : "Not built after the automatic retries", null)!;
}
