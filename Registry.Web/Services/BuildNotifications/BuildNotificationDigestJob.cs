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
using Registry.Web.Models.Email;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>
/// Sends one user the build notification e-mail of the lines waiting for them. The lines are claimed first, with one
/// conditional update (<see cref="BuildNotificationOutbox.ClaimAsync"/>): when two hosts scheduled a digest for the same
/// user, the second run finds nothing and ends. The preference is read again at send time, so a user who switched the
/// e-mails off (or to failures only) in the window gets what they chose then. The e-mail is queued
/// (<see cref="IEmailService.QueueAsync(Models.Email.EmailMessage, CancellationToken)"/>), whose job retries the mail
/// server's transient failures itself, in the unit that marks its lines sent (<see cref="BuildNotificationOutbox.HandOverAsync"/>).
/// </summary>
/// <remarks>
/// A failure before the e-mail is queued (the database or the job storage unavailable) gives the claimed lines back and
/// is retried a few times within about twenty minutes; past that the daily cleanup finds the lines still waiting and
/// schedules the digest again (<see cref="BuildNotificationCleanup"/>). Lines whose e-mail is queued are never given back.
/// Runs on the default queue, like the e-mail job.
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

        var sent = false;
        var queued = false;
        try
        {
            if (await ComposeAsync(userId, lines, cancellationToken).ConfigureAwait(false) is { } digest)
            {
                // The lines are marked sent in the unit that queues their e-mail: a mark that fails never follows a
                // queued e-mail, which a retry would send again
                await _outbox.HandOverAsync(claimId, async () =>
                {
                    await _email.QueueAsync(digest.Message, CancellationToken.None).ConfigureAwait(false);
                    queued = true;
                }).ConfigureAwait(false);
                _logger.LogInformation("Build notification e-mail with {Count} build(s) queued for user {UserId}",
                    digest.Builds, userId);
            }
            else
                await _outbox.MarkSentAsync(claimId, cancellationToken).ConfigureAwait(false);

            sent = true;
        }
        finally
        {
            // Not cancellable: a run stopped by a shutdown must still give the lines to its retry. Once their e-mail is
            // queued they are never given back: the retry must not send them again
            if (!sent && !queued)
                await _outbox.ReleaseAsync(claimId, CancellationToken.None).ConfigureAwait(false);
            else if (!sent)
                _logger.LogError("The build notification e-mail of user {UserId} is queued but its lines could not be " +
                                 "marked sent: they keep their claim ({ClaimId}), so this digest's retry does not send " +
                                 "them again", userId, claimId);
        }

        // The recorder of a line added while this run held the others left its digest to this run
        if (await _outbox.FirstUnclaimedAsync(userId, cancellationToken).ConfigureAwait(false) is { } first)
            _digests.Schedule(userId, first);
    }

    // The e-mail of the lines and how many builds it tells; null when there is none to send (the lines are dropped)
    private async Task<(EmailMessage Message, int Builds)?> ComposeAsync(string userId,
        IReadOnlyList<BuildNotification> lines, CancellationToken ct)
    {
        var level = await _levels.OfAsync(userId, ct).ConfigureAwait(false);
        // A line of a dataset deleted since names it by its tombstone: nothing to tell about it
        var builds = lines.Where(l => !SlugKeyedRows.IsTombstone(l.DsSlug)).Select(ToLine).OfType<BuildNotificationLine>()
            .Where(b => level.Wants(b.Outcome)).ToList();
        if (builds.Count == 0 || !_email.IsEnabled)
        {
            _logger.LogInformation("{Count} build notification(s) of user {UserId} dropped: the user no longer wants them " +
                                   "({Level}) or e-mail is off", lines.Count, userId, level);
            return null;
        }

        var recipient = await _recipients.FindAsync(userId, ct).ConfigureAwait(false);
        if (recipient == null)
        {
            _logger.LogInformation("{Count} build notification(s) of user {UserId} dropped: the user cannot be e-mailed " +
                                   "(deactivated, no valid address or unknown)", lines.Count, userId);
            return null;
        }

        var message = await _composer.ComposeAsync(recipient,
            new BuildNotificationModel(recipient.DisplayName ?? userId, builds)).ConfigureAwait(false);
        return (message, builds.Count);
    }

    private static BuildNotificationLine? ToLine(BuildNotification line) =>
        Enum.TryParse<BuildOutcome>(line.Outcome, out var outcome)
            ? new BuildNotificationLine(line.OrgSlug, line.DsSlug, line.Path, outcome, line.Error,
                DateTime.SpecifyKind(line.FinishedAtUtc, DateTimeKind.Utc))
            : null;
}
