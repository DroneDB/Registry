using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Email;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>Whom a digest goes to and the builds it tells.</summary>
/// <param name="Recipient">The user's address.</param>
/// <param name="Builds">The builds, at least one.</param>
public sealed record BuildNotificationDelivery(EmailAddress Recipient, IReadOnlyList<BuildNotificationLine> Builds);

/// <summary>
/// What one user may still be told of the lines waiting for them, decided when the digest is sent, not when the lines
/// were added: the preference of that moment (turned off, or set to failures only), an address to write to (not
/// deactivated, one valid mailbox), and the datasets the user can still read (removed from the organization since, or the
/// dataset made private, their slugs, paths and errors are not theirs to see any more). A line of a dataset deleted since
/// is left out too.
/// </summary>
public sealed class BuildNotificationAudience
{
    private readonly IBuildNotificationLevels _levels;
    private readonly IUserRecipients _recipients;
    private readonly IUserDatasetAccess _datasets;
    private readonly ILogger<BuildNotificationAudience> _logger;

    public BuildNotificationAudience(IBuildNotificationLevels levels, IUserRecipients recipients,
        IUserDatasetAccess datasets, ILogger<BuildNotificationAudience> logger)
    {
        _levels = levels ?? throw new ArgumentNullException(nameof(levels));
        _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        _datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The delivery of <paramref name="lines"/> to <paramref name="userId"/>; null when nothing of them is to be sent.</summary>
    public async Task<BuildNotificationDelivery?> OfAsync(string userId, IReadOnlyList<BuildNotification> lines,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentNullException.ThrowIfNull(lines);

        var level = await _levels.OfAsync(userId, ct).ConfigureAwait(false);
        // A line of a dataset deleted since names it by its tombstone: nothing to tell about it
        var builds = lines.Where(l => !SlugKeyedRows.IsTombstone(l.DsSlug)).Select(ToLine).OfType<BuildNotificationLine>()
            .Where(b => level.Wants(b.Outcome)).ToList();
        if (builds.Count == 0)
        {
            _logger.LogInformation("{Count} build notification(s) of user {UserId} dropped: the user no longer wants them " +
                                   "({Level})", lines.Count, userId, level);
            return null;
        }

        var recipient = await _recipients.FindAsync(userId, ct).ConfigureAwait(false);
        if (recipient == null)
        {
            _logger.LogInformation("{Count} build notification(s) of user {UserId} dropped: the user cannot be e-mailed " +
                                   "(deactivated, no valid address or unknown)", lines.Count, userId);
            return null;
        }

        var readable = await ReadableAsync(userId, builds, ct).ConfigureAwait(false);
        if (readable.Count < builds.Count)
            _logger.LogInformation("{Count} build notification(s) of user {UserId} dropped: the user can no longer read " +
                                   "their dataset", builds.Count - readable.Count, userId);

        return readable.Count == 0 ? null : new BuildNotificationDelivery(recipient, readable);
    }

    // The builds of the datasets the user can read now, each dataset asked once
    private async Task<List<BuildNotificationLine>> ReadableAsync(string userId, List<BuildNotificationLine> builds,
        CancellationToken ct)
    {
        var canRead = new Dictionary<(string Org, string Ds), bool>();
        foreach (var dataset in builds.Select(b => (b.OrgSlug, b.DsSlug)).Distinct())
            canRead[dataset] = await _datasets.CanReadAsync(userId, dataset.OrgSlug, dataset.DsSlug, ct).ConfigureAwait(false);

        return builds.Where(b => canRead[(b.OrgSlug, b.DsSlug)]).ToList();
    }

    private static BuildNotificationLine? ToLine(BuildNotification line) =>
        Enum.TryParse<BuildOutcome>(line.Outcome, out var outcome)
            ? new BuildNotificationLine(line.OrgSlug, line.DsSlug, line.Path, outcome, line.Error,
                DateTime.SpecifyKind(line.FinishedAtUtc, DateTimeKind.Utc))
            : null;
}
