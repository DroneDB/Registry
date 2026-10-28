#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>
/// <see cref="IBuildNotificationRecorder"/>: the line goes to the outbox when e-mail is on, somebody asked for the build
/// and their preference wants the outcome; the first waiting line of a user schedules their digest
/// (<see cref="BuildNotificationDigestJob"/>) <see cref="BuildNotificationOptions.DigestMinutes"/> later. A line added
/// while a digest is sending waits for the next digest, which the sending run schedules when it is done. A build that
/// waits for files is recorded whatever the preference (unsent only when it wants it): the record tells the wait of
/// that content once and names who the build that follows the files answers; it ends when the build of that content
/// ends for good.
/// </summary>
public sealed class BuildNotificationRecorder : IBuildNotificationRecorder
{
    private readonly IEmailService _email;
    private readonly IBuildRequesterResolver _requesters;
    private readonly IBuildNotificationLevels _levels;
    private readonly BuildNotificationOutbox _outbox;
    private readonly BuildNotificationDigests _digests;
    private readonly ILogger<BuildNotificationRecorder> _logger;

    public BuildNotificationRecorder(IEmailService email, IBuildRequesterResolver requesters,
        IBuildNotificationLevels levels, BuildNotificationOutbox outbox, BuildNotificationDigests digests,
        ILogger<BuildNotificationRecorder> logger)
    {
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _requesters = requesters ?? throw new ArgumentNullException(nameof(requesters));
        _levels = levels ?? throw new ArgumentNullException(nameof(levels));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _digests = digests ?? throw new ArgumentNullException(nameof(digests));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task RecordAsync(BuildReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Before any query: with e-mail off nothing could ever be sent
        if (!_email.IsEnabled)
            return;

        // Who asked is read first: a build that follows a wait for files answers the request of that wait
        var requester = await _requesters.ResolveAsync(report, ct).ConfigureAwait(false);

        // The build of this content ended for good: a later wait of it is news again
        if (report.Outcome != BuildOutcome.WaitingForFiles)
            await _outbox.EndWaitAsync(report.OrgSlug, report.DsSlug, report.Path, report.Hash, ct).ConfigureAwait(false);

        if (requester == null)
            return;

        var level = await _levels.OfAsync(requester.UserId, ct).ConfigureAwait(false);
        if (!level.Wants(report.Outcome))
        {
            // A wait nobody is told still names who the build that follows the files answers
            if (report.Outcome == BuildOutcome.WaitingForFiles)
                await _outbox.AddUnwantedWaitAsync(report, requester, ct).ConfigureAwait(false);
            return;
        }

        var line = await _outbox.AddAsync(report, requester, ct).ConfigureAwait(false);
        if (line == null)
        {
            _logger.LogDebug("Build notification of {Org}/{Ds}/{Path} ({Outcome}) for user {UserId} is already there",
                report.OrgSlug, report.DsSlug, report.Path, report.Outcome, requester.UserId);
            return;
        }

        _logger.LogInformation("Build notification of {Org}/{Ds}/{Path} ({Outcome}) recorded for user {UserId}",
            report.OrgSlug, report.DsSlug, report.Path, report.Outcome, requester.UserId);

        // Any other waiting line already has its digest: scheduled, or the run sending it schedules the next one
        if (!await _outbox.HasOtherUnsentAsync(requester.UserId, line.Id, ct).ConfigureAwait(false))
            _digests.Schedule(requester.UserId, line.FinishedAtUtc);
    }
}
