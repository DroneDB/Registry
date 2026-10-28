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
/// ones. The line of a build that waits for files is also the record of that wait until the build ends: it tells the
/// wait of a content once, and names who the build that follows the files answers. Every rule that keeps two hosts from sending a line twice is a condition of one statement here, never a check
/// followed by a write.
/// </summary>
public sealed class BuildNotificationOutbox
{
    // The outcome of the lines that record a wait for files, as stored
    private const string Waiting = nameof(BuildOutcome.WaitingForFiles);

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
    /// waiting for that user (<see cref="BuildNotification.PendingKey"/>), the request already has a line with this
    /// outcome (<see cref="BuildNotification.RequestJobId"/>), or, for a build waiting for files, the same content of the
    /// entry already waited (see <see cref="WaitRequesterAsync"/>). The unique keys decide, so two hosts adding the same
    /// line at once add it once.
    /// </summary>
    /// <returns>The line added, or null when it was already there.</returns>
    public Task<BuildNotification?> AddAsync(BuildReport report, BuildRequester requester, CancellationToken ct) =>
        InsertAsync(LineOf(report, requester, wanted: true), ct);

    /// <summary>
    /// Keeps the wait of <paramref name="report"/> (<see cref="BuildOutcome.WaitingForFiles"/>) for a
    /// <paramref name="requester"/> whose preference does not want it: a line already sent, which no e-mail tells, but
    /// which links the build that follows the files to that user (<see cref="WaitRequesterAsync"/>) and tells the wait of
    /// this content once even when they want it later.
    /// </summary>
    /// <returns>The line added, or null when the content already waited.</returns>
    public Task<BuildNotification?> AddUnwantedWaitAsync(BuildReport report, BuildRequester requester, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Outcome != BuildOutcome.WaitingForFiles)
            throw new ArgumentException($"Only a {nameof(BuildOutcome.WaitingForFiles)} report is kept unsent", nameof(report));

        return InsertAsync(LineOf(report, requester, wanted: false), ct);
    }

    private BuildNotification LineOf(BuildReport report, BuildRequester requester, bool wanted)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(requester);

        var now = UtcNow;
        return new BuildNotification
        {
            UserId = requester.UserId,
            OrgSlug = report.OrgSlug,
            DsSlug = report.DsSlug,
            Path = report.Path,
            Hash = report.Hash,
            Outcome = report.Outcome.ToString(),
            Error = report.Outcome is BuildOutcome.Failed or BuildOutcome.WaitingForFiles ? report.Error : null,
            RequestJobId = requester.RequestJobId,
            FinishedAtUtc = now,
            PendingKey = wanted ? PendingKeyOf(requester.UserId, report) : null,
            SentAtUtc = wanted ? null : now
        };
    }

    private async Task<BuildNotification?> InsertAsync(BuildNotification line, CancellationToken ct)
    {
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

    private Task<bool> IsKnownAsync(BuildNotification line, CancellationToken ct)
    {
        var waits = line.Outcome == Waiting;
        return _db.BuildNotifications.AsNoTracking().AnyAsync(n =>
            (line.PendingKey != null && n.PendingKey == line.PendingKey) ||
            (line.RequestJobId != null && n.RequestJobId == line.RequestJobId && n.Outcome == line.Outcome) ||
            (waits && n.Outcome == Waiting && n.OrgSlug == line.OrgSlug && n.DsSlug == line.DsSlug &&
             n.Path == line.Path && n.Hash == line.Hash), ct);
    }

    /// <summary>
    /// The request whose build of <paramref name="hash"/> of the entry <paramref name="path"/> found files missing first
    /// and still waits (the first line of that wait, wanted or not, sent or not); null when no request of it waits. The
    /// build that runs when the files arrive (as the build service) answers that request.
    /// </summary>
    public async Task<BuildRequester?> WaitRequesterAsync(string orgSlug, string dsSlug, string path, string hash,
        CancellationToken ct)
    {
        var wait = await WaitsOf(orgSlug, dsSlug, path, hash).AsNoTracking().OrderBy(n => n.Id)
            .Select(n => new { n.UserId, n.RequestJobId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return wait == null ? null : new BuildRequester(wait.UserId, wait.RequestJobId);
    }

    /// <summary>
    /// Ends the wait of <paramref name="hash"/> of the entry <paramref name="path"/>: its build ended for good (built or
    /// failed), so the lines of the wait go, sent or not (news that a build waits is stale once it ended), and a later
    /// wait of the same content is news again.
    /// </summary>
    /// <returns>How many lines were removed.</returns>
    public Task<int> EndWaitAsync(string orgSlug, string dsSlug, string path, string hash, CancellationToken ct) =>
        WaitsOf(orgSlug, dsSlug, path, hash).ExecuteDeleteAsync(ct);

    private IQueryable<BuildNotification> WaitsOf(string orgSlug, string dsSlug, string path, string hash) =>
        _db.BuildNotifications.Where(n => n.Outcome == Waiting && n.OrgSlug == orgSlug && n.DsSlug == dsSlug &&
                                          n.Path == path && n.Hash == hash);

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

    /// <summary>
    /// Deletes the lines sent before <paramref name="cutoffUtc"/>, except the lines of a wait for files that has not ended
    /// (<see cref="EndWaitAsync"/>), however old: they tell the wait once and name who the build that follows answers.
    /// Those of a deleted dataset go with the others.
    /// </summary>
    /// <returns>How many were deleted.</returns>
    public Task<int> DeleteSentBeforeAsync(DateTime cutoffUtc, CancellationToken ct) =>
        _db.BuildNotifications.Where(n => n.SentAtUtc != null && n.SentAtUtc < cutoffUtc &&
                                          (n.Outcome != Waiting || n.DsSlug.StartsWith(SlugKeyedRows.TombstonePrefix)))
            .ExecuteDeleteAsync(ct);

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
