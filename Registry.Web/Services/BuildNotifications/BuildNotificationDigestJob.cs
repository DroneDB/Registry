#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>
/// Sends one user the build notification e-mail of the lines waiting for them. The lines are claimed first, with one
/// conditional update (<see cref="BuildNotificationOutbox.ClaimAsync"/>): when two hosts scheduled a digest for the same
/// user, the second run finds nothing and ends. The preference is read again at send time, so a user who switched the
/// e-mails off (or to failures only) in the window gets what they chose then. The e-mail is queued
/// (<see cref="IEmailService.QueueAsync(Models.Email.EmailMessage, CancellationToken)"/>), whose job retries the mail
/// server's transient failures itself.
/// </summary>
/// <remarks>
/// A failure here (the database or the job storage unavailable) gives the claimed lines back and is retried a few times
/// within about twenty minutes; past that the daily cleanup finds the lines still waiting and schedules the digest
/// again (<see cref="BuildNotificationCleanup"/>). Runs on the default queue, like the e-mail job.
/// </remarks>
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 300, 900])]
public class BuildNotificationDigestJob
{
    private readonly BuildNotificationOutbox _outbox;
    private readonly IBuildNotificationLevels _levels;
    private readonly IUserRecipients _recipients;
    private readonly IBuildNotificationComposer _composer;
    private readonly IEmailService _email;
    private readonly BuildNotificationDigests _digests;
    private readonly ILogger<BuildNotificationDigestJob> _logger;

    public BuildNotificationDigestJob(BuildNotificationOutbox outbox, IBuildNotificationLevels levels,
        IUserRecipients recipients, IBuildNotificationComposer composer, IEmailService email,
        BuildNotificationDigests digests, ILogger<BuildNotificationDigestJob> logger)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _levels = levels ?? throw new ArgumentNullException(nameof(levels));
        _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _digests = digests ?? throw new ArgumentNullException(nameof(digests));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Sends <paramref name="userId"/> the lines waiting for them, then schedules the next digest when more arrived meanwhile.</summary>
    public async Task SendAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        var claimId = Guid.NewGuid().ToString("N");
        var lines = await _outbox.ClaimAsync(userId, claimId, cancellationToken).ConfigureAwait(false);
        if (lines.Count == 0)
        {
            _logger.LogDebug("No build notification waits for user {UserId} (another digest took them)", userId);
            return;
        }

        var done = false;
        try
        {
            await SendLinesAsync(userId, lines, cancellationToken).ConfigureAwait(false);
            await _outbox.MarkSentAsync(claimId, cancellationToken).ConfigureAwait(false);
            done = true;
        }
        finally
        {
            // Not cancellable: a run stopped by a shutdown must still give the lines to its retry
            if (!done)
                await _outbox.ReleaseAsync(claimId, CancellationToken.None).ConfigureAwait(false);
        }

        // The recorder of a line added while this run held the others left its digest to this run
        if (await _outbox.FirstUnclaimedAsync(userId, cancellationToken).ConfigureAwait(false) is { } first)
            _digests.Schedule(userId, first);
    }

    private async Task SendLinesAsync(string userId, IReadOnlyList<BuildNotification> lines, CancellationToken ct)
    {
        var level = await _levels.OfAsync(userId, ct).ConfigureAwait(false);
        // A line of a dataset deleted since names it by its tombstone: nothing to tell about it
        var builds = lines.Where(l => !SlugKeyedRows.IsTombstone(l.DsSlug)).Select(ToLine).OfType<BuildNotificationLine>()
            .Where(b => level.Wants(b.Outcome)).ToList();
        if (builds.Count == 0 || !_email.IsEnabled)
        {
            _logger.LogInformation("{Count} build notification(s) of user {UserId} dropped: the user no longer wants them " +
                                   "({Level}) or e-mail is off", lines.Count, userId, level);
            return;
        }

        var recipient = await _recipients.FindAsync(userId, ct).ConfigureAwait(false);
        if (recipient == null)
        {
            _logger.LogInformation("{Count} build notification(s) of user {UserId} dropped: the user cannot be e-mailed " +
                                   "(deactivated, no valid address or unknown)", lines.Count, userId);
            return;
        }

        var message = await _composer.ComposeAsync(recipient,
            new BuildNotificationModel(recipient.DisplayName ?? userId, builds)).ConfigureAwait(false);
        await _email.QueueAsync(message, ct).ConfigureAwait(false);

        _logger.LogInformation("Build notification e-mail with {Count} build(s) queued for user {UserId}", builds.Count, userId);
    }

    private static BuildNotificationLine? ToLine(BuildNotification line) =>
        Enum.TryParse<BuildOutcome>(line.Outcome, out var outcome)
            ? new BuildNotificationLine(line.OrgSlug, line.DsSlug, line.Path, outcome, line.Error,
                DateTime.SpecifyKind(line.FinishedAtUtc, DateTimeKind.Utc))
            : null;
}
