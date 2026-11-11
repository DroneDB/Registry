using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>
/// The daily care of the build notification outbox, run with the job history cleanup: the sent lines are deleted after
/// <see cref="SentRetention"/>, and the lines still waiting <see cref="OverdueAfter"/> after they ended (their digest was
/// lost: the host stopped first, or the run died holding them) get a digest now.
/// </summary>
public sealed class BuildNotificationCleanup
{
    /// <summary>How long a sent line is kept: what stops the same request from being reported again.</summary>
    public static readonly TimeSpan SentRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// How long after it ended a waiting line counts as lost: well past the longest digest window, so a digest that is
    /// only due is never sent early.
    /// </summary>
    public static readonly TimeSpan OverdueAfter = TimeSpan.FromHours(1);

    private readonly BuildNotificationOutbox _outbox;
    private readonly BuildNotificationDigests _digests;
    private readonly TimeProvider _time;

    public BuildNotificationCleanup(BuildNotificationOutbox outbox, BuildNotificationDigests digests, TimeProvider time)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _digests = digests ?? throw new ArgumentNullException(nameof(digests));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Deletes the old sent lines and schedules the digests that were lost.</summary>
    public async Task RunAsync(JobConsole console, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(console);
        var now = _time.GetUtcNow().UtcDateTime;

        var deleted = await _outbox.DeleteSentBeforeAsync(now - SentRetention, ct).ConfigureAwait(false);

        var overdue = await _outbox.OverdueUsersAsync(now - OverdueAfter, ct).ConfigureAwait(false);
        foreach (var userId in overdue)
            _digests.Schedule(userId, DateTime.MinValue);

        console.Info("Build notifications: {Deleted} sent line(s) older than {Days} days removed, {Overdue} lost digest(s) " +
                     "scheduled", deleted, SentRetention.TotalDays, overdue.Count);
    }
}
