#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>
/// The lines of the build notification e-mails in the Registry database (<see cref="BuildNotification"/>), shared by every
/// host: the recorder adds them, a digest run claims and sends the lines of one user, the daily cleanup removes the old
/// ones. Every rule that keeps two hosts from sending a line twice is a condition of one statement here, never a check
/// followed by a write.
/// </summary>
public sealed class BuildNotificationOutbox
{
    private readonly RegistryContext _db;
    private readonly TimeProvider _time;

    public BuildNotificationOutbox(RegistryContext db, TimeProvider time)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Adds the line of <paramref name="report"/> for <paramref name="requester"/>, unless the same news is already
    /// waiting for that user (<see cref="BuildNotification.PendingKey"/>) or the request already has a line with this
    /// outcome (<see cref="BuildNotification.RequestJobId"/>). The unique keys decide, so two hosts adding the same
    /// line at once add it once.
    /// </summary>
    /// <returns>The line added, or null when it was already there.</returns>
    public async Task<BuildNotification?> AddAsync(BuildReport report, BuildRequester requester, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(requester);

        var outcome = report.Outcome.ToString();
        var line = new BuildNotification
        {
            UserId = requester.UserId,
            OrgSlug = report.OrgSlug,
            DsSlug = report.DsSlug,
            Path = report.Path,
            Hash = report.Hash,
            Outcome = outcome,
            Error = report.Outcome == BuildOutcome.Failed ? report.Error : null,
            RequestJobId = requester.RequestJobId,
            FinishedAtUtc = UtcNow,
            PendingKey = PendingKeyOf(requester.UserId, report)
        };

        // The common case of a repeat (a state applied twice, a retry chain giving up again) without a failed insert
        if (await IsKnownAsync(line, ct).ConfigureAwait(false))
            return null;

        _db.BuildNotifications.Add(line);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return line;
        }
        catch (DbUpdateException)
        {
            // Another host added the same line between the check and the insert: the unique key refused this one
            _db.Entry(line).State = EntityState.Detached;
            if (await IsKnownAsync(line, ct).ConfigureAwait(false))
                return null;
            throw;
        }
    }

    private Task<bool> IsKnownAsync(BuildNotification line, CancellationToken ct) =>
        _db.BuildNotifications.AsNoTracking().AnyAsync(n =>
            n.PendingKey == line.PendingKey ||
            (line.RequestJobId != null && n.RequestJobId == line.RequestJobId && n.Outcome == line.Outcome), ct);

    /// <summary>
    /// Makes the lines that answer the job <paramref name="jobId"/> answer no request any more: the job runs again from
    /// its failure (a user's retry), and the end of that run is a new request's news, even when it is the outcome already
    /// sent. A line still waiting keeps its <see cref="BuildNotification.PendingKey"/>, so the same news is not added twice.
    /// </summary>
    public Task RenewRequestAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        return _db.BuildNotifications.Where(n => n.RequestJobId == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.RequestJobId, (string?)null), ct);
    }

    /// <summary>Whether <paramref name="userId"/> has an unsent line other than <paramref name="exceptId"/> (claimed or not).</summary>
    public Task<bool> HasOtherUnsentAsync(string userId, long exceptId, CancellationToken ct) =>
        _db.BuildNotifications.AsNoTracking()
            .AnyAsync(n => n.UserId == userId && n.SentAtUtc == null && n.Id != exceptId, ct);

    /// <summary>
    /// Claims every unsent line of <paramref name="userId"/> that no run holds, for the run <paramref name="claimId"/>,
    /// with one conditional update: two runs for the same user (two hosts recorded at the same moment) never get the same
    /// line, the second one gets none.
    /// </summary>
    /// <returns>The lines claimed.</returns>
    public async Task<IReadOnlyList<BuildNotification>> ClaimAsync(string userId, string claimId, CancellationToken ct)
    {
        var now = UtcNow;
        var claimed = await _db.BuildNotifications
            .Where(n => n.UserId == userId && n.SentAtUtc == null && n.ClaimId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ClaimId, claimId).SetProperty(n => n.ClaimedAtUtc, now), ct)
            .ConfigureAwait(false);

        if (claimed == 0)
            return [];

        return await _db.BuildNotifications.AsNoTracking()
            .Where(n => n.ClaimId == claimId && n.SentAtUtc == null)
            .OrderBy(n => n.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Marks the lines of the run <paramref name="claimId"/> sent: the same news may wait again from now on.</summary>
    public Task MarkSentAsync(string claimId, CancellationToken ct)
    {
        var now = UtcNow;
        return _db.BuildNotifications
            .Where(n => n.ClaimId == claimId && n.SentAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.SentAtUtc, now).SetProperty(n => n.PendingKey, (string?)null), ct);
    }

    /// <summary>
    /// Marks the lines of the run <paramref name="claimId"/> sent and hands them over with <paramref name="handOver"/> (the
    /// e-mail queued) as one unit: the mark is written in a transaction, the hand-over runs inside it, and the lines count
    /// as sent only when that transaction commits. A failure before the hand-over is done leaves the lines unsent and
    /// nothing handed over, so a retry sends them once; a mark that fails never follows a queued e-mail. A transient
    /// failure of the commit is replayed by the execution strategy (MySQL), which writes the mark again but never runs
    /// <paramref name="handOver"/> a second time.
    /// </summary>
    /// <remarks>
    /// Not cancellable once started: a stop between the hand-over and the commit would leave an e-mail queued for lines
    /// still unsent. The one case left is a commit that fails for good after the hand-over: the caller must then keep the
    /// claim (never give the lines back), so its own retry cannot send them again.
    /// </remarks>
    public Task HandOverAsync(string claimId, Func<Task> handOver)
    {
        ArgumentException.ThrowIfNullOrEmpty(claimId);
        ArgumentNullException.ThrowIfNull(handOver);

        var handedOver = false;
        // A retrying strategy refuses a transaction it does not own: it runs the whole unit, and replays it after a
        // transient failure (verifying first whether the commit went through)
        return _db.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
            async ct =>
            {
                await MarkSentAsync(claimId, ct).ConfigureAwait(false);
                if (handedOver)
                    return;
                await handOver().ConfigureAwait(false);
                handedOver = true;
            },
            async ct => !await _db.BuildNotifications.AsNoTracking()
                .AnyAsync(n => n.ClaimId == claimId && n.SentAtUtc == null, ct).ConfigureAwait(false),
            CancellationToken.None);
    }

    /// <summary>Gives the unsent lines of the run <paramref name="claimId"/> back, for the next run.</summary>
    public Task ReleaseAsync(string claimId, CancellationToken ct) =>
        _db.BuildNotifications
            .Where(n => n.ClaimId == claimId && n.SentAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ClaimId, (string?)null)
                .SetProperty(n => n.ClaimedAtUtc, (DateTime?)null), ct);

    /// <summary>When the oldest unsent line of <paramref name="userId"/> that no run holds ended; null when there is none.</summary>
    public Task<DateTime?> FirstUnclaimedAsync(string userId, CancellationToken ct) =>
        _db.BuildNotifications.AsNoTracking()
            .Where(n => n.UserId == userId && n.SentAtUtc == null && n.ClaimId == null)
            .MinAsync(n => (DateTime?)n.FinishedAtUtc, ct);

    /// <summary>Deletes the lines sent before <paramref name="cutoffUtc"/>.</summary>
    /// <returns>How many were deleted.</returns>
    public Task<int> DeleteSentBeforeAsync(DateTime cutoffUtc, CancellationToken ct) =>
        _db.BuildNotifications.Where(n => n.SentAtUtc != null && n.SentAtUtc < cutoffUtc).ExecuteDeleteAsync(ct);

    /// <summary>
    /// The users whose lines still wait although they ended before <paramref name="cutoffUtc"/>: their digest was lost
    /// (a host stopped before it ran, or a run died holding them). The claims of such runs made before the cutoff are
    /// released first, so the next digest of those users can take the lines.
    /// </summary>
    public async Task<IReadOnlyList<string>> OverdueUsersAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        await _db.BuildNotifications
            .Where(n => n.SentAtUtc == null && n.ClaimId != null && n.ClaimedAtUtc < cutoffUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ClaimId, (string?)null)
                .SetProperty(n => n.ClaimedAtUtc, (DateTime?)null), ct)
            .ConfigureAwait(false);

        return await _db.BuildNotifications.AsNoTracking()
            .Where(n => n.SentAtUtc == null && n.ClaimId == null && n.FinishedAtUtc < cutoffUtc)
            .Select(n => n.UserId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // Who, which entry and which outcome; hashed to a fixed length that any database can index
    private static string PendingKeyOf(string userId, BuildReport report) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\0', userId, report.OrgSlug, report.DsSlug, report.Path, report.Hash, report.Outcome))));
}
