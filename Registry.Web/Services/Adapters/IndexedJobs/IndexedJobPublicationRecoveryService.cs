#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;
using Registry.Web.Attributes;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>Why a recovery pass stopped.</summary>
internal enum RecoveryStopReason
{
    /// <summary>No due member is left.</summary>
    Drained,

    /// <summary><see cref="IndexedJobPublicationRecoveryService.MaxCandidatesPerPass"/> candidates were examined.</summary>
    CandidateLimit,

    /// <summary><see cref="IndexedJobPublicationRecoveryService.PassBudget"/> elapsed.</summary>
    TimeBudget,

    /// <summary>Job storage failed while reading the set or rescheduling a member.</summary>
    StorageError,

    /// <summary>The same member was returned twice in one pass (safety net against a non-advancing traversal).</summary>
    RepeatGuard,

    /// <summary>The job was asked to stop (server shutdown).</summary>
    Cancelled
}

/// <summary>Counters of one recovery pass.</summary>
internal sealed class RecoveryPassSummary
{
    public int Examined { get; set; }
    public int Published { get; set; }
    public int Deferred { get; set; }
    public int Cancelled { get; set; }
    public int StaleRemoved { get; set; }
    public RecoveryStopReason StopReason { get; set; } = RecoveryStopReason.Drained;

    public override string ToString() =>
        $"examined={Examined} published={Published} deferred={Deferred} cancelled={Cancelled} " +
        $"staleRemoved={StaleRemoved} stop={StopReason}";
}

/// <summary>
/// Completes publications of indexed jobs stranded in <see cref="IndexingState"/> (for example by a
/// crash between job creation and its transition to Enqueued/Scheduled). Due members of the
/// <see cref="IndexingStateHandler.RecoverySetKey"/> set are visited one at a time by lowest score;
/// each job is re-published on its own id from the record in its state data, rescheduled with a
/// backoff while it cannot be published, or cancelled when its record is unusable. All steps are
/// idempotent and guarded by expected-state transitions, so overlapping passes and a still-running
/// publisher converge without locks. Storage failures never cancel a job: the member stays in the
/// set and the pass ends. That is also why it has no <see cref="DisableConcurrentExecutionAttribute"/>:
/// a pass that overlaps the next minute's is safe and bounded (<see cref="PassBudget"/>).
/// </summary>
/// <remarks>Not retried: the next scheduled pass is the retry.</remarks>
[JobExpiration(ExpirationTimeoutInMinutes = 60)]
[AutomaticRetry(Attempts = 0)]
public class IndexedJobPublicationRecoveryService
{
    /// <summary>Maximum number of candidates examined in one pass.</summary>
    internal const int MaxCandidatesPerPass = 100;

    /// <summary>Time after which a pass stops picking new candidates.</summary>
    internal static readonly TimeSpan PassBudget = TimeSpan.FromSeconds(30);

    /// <summary>JobIndex error type recorded for a job cancelled because of its publication record.</summary>
    internal const string InvalidRecordErrorType = "InvalidPublicationRecord";

    /// <summary>
    /// Maximum length of a Hangfire state reason written by this feature. Hangfire.MySqlStorage 2.0.3
    /// declares <c>State.Reason</c> as <c>nvarchar(100)</c> (embedded Install.sql); a longer reason makes
    /// the transition throw "Data too long for column 'Reason'", so the cancellation would defer forever.
    /// </summary>
    internal const int MaxStateReasonLength = 100;

    /// <summary>Prefix of the reason stored on a job cancelled because of its publication record.</summary>
    internal const string InvalidRecordReasonPrefix = "Invalid publication record";

    /// <summary>Short, stable reason categories stored in Hangfire; the full detail goes to the log only.</summary>
    internal const string MalformedRecordCategory = "malformed";
    internal const string UnsupportedDestinationCategory = "unsupported destination";
    internal const string IncompatibleIdentityCategory = "incompatible JobIndex identity";

    private enum CandidateOutcome
    {
        Published,
        Deferred,
        Cancelled,
        StaleRemoved,

        /// <summary>A required set write failed; the pass must end.</summary>
        StorageFailed
    }

    private readonly IJobIndexWriter _writer;
    private readonly ILogger<IndexedJobPublicationRecoveryService> _logger;
    private readonly JobStorage _storage;
    private readonly TimeProvider _time;

    public IndexedJobPublicationRecoveryService(IJobIndexWriter writer,
        ILogger<IndexedJobPublicationRecoveryService> logger, JobStorage storage, TimeProvider? time = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The job as stored before RQ-09 (D4): runs with the context's shutdown token.</summary>
    public Task RecoverPublications(PerformContext? context) => RecoverPublications(context, JobCancellation.Of(context));

    /// <summary>Recurring entry point: one bounded pass.</summary>
    public async Task RecoverPublications(PerformContext? context, CancellationToken cancellationToken)
    {
        var storage = context?.Storage ?? _storage;
        var client = new BackgroundJobClient(storage);

        var summary = await RunPassAsync(storage, client, cancellationToken);

        var console = JobConsole.For(_logger, context);
        if (summary.Examined > 0 || summary.StopReason != RecoveryStopReason.Drained)
            console.Info("Indexed job publication recovery: {Summary}", summary);
        else
            console.Debug("Indexed job publication recovery: {Summary}", summary);
    }

    /// <summary>
    /// Runs one bounded pass over the due members of the recovery set.
    /// </summary>
    internal async Task<RecoveryPassSummary> RunPassAsync(JobStorage storage, IBackgroundJobClient client,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(client);

        var summary = new RecoveryPassSummary();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var started = _time.GetTimestamp();

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                summary.StopReason = RecoveryStopReason.Cancelled;
                break;
            }

            if (summary.Examined >= MaxCandidatesPerPass)
            {
                summary.StopReason = RecoveryStopReason.CandidateLimit;
                break;
            }

            if (_time.GetElapsedTime(started) >= PassBudget)
            {
                summary.StopReason = RecoveryStopReason.TimeBudget;
                break;
            }

            var now = UtcNow();
            string? jobId;
            try
            {
                // Bounded, limit-1 read: the whole set is never loaded and scores are never read back
                // (their text form is not exact on every storage).
                using var connection = storage.GetConnection();
                jobId = connection.GetFirstByLowestScoreFromSet(IndexingStateHandler.RecoverySetKey, 0,
                    IndexingStateHandler.ScoreForNow(now));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cannot read the indexed job publication recovery set; ending the pass");
                summary.StopReason = RecoveryStopReason.StorageError;
                break;
            }

            if (jobId is null)
            {
                summary.StopReason = RecoveryStopReason.Drained;
                break;
            }

            if (!seen.Add(jobId))
            {
                _logger.LogWarning("Indexed job {JobId} was returned twice by the recovery set in one pass; ending the pass",
                    jobId);
                summary.StopReason = RecoveryStopReason.RepeatGuard;
                break;
            }

            summary.Examined++;

            var outcome = await ProcessCandidateAsync(jobId, storage, client, now, ct);
            switch (outcome)
            {
                case CandidateOutcome.Published: summary.Published++; break;
                case CandidateOutcome.Deferred: summary.Deferred++; break;
                case CandidateOutcome.Cancelled: summary.Cancelled++; break;
                case CandidateOutcome.StaleRemoved: summary.StaleRemoved++; break;
                case CandidateOutcome.StorageFailed:
                    summary.StopReason = RecoveryStopReason.StorageError;
                    return summary;
            }
        }

        return summary;
    }

    /// <summary>
    /// Delay before a job still in <c>Indexing</c> is examined again, by age since its original creation.
    /// </summary>
    internal static TimeSpan RetryDelayFor(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(3)) return TimeSpan.FromMinutes(1);
        if (age < TimeSpan.FromMinutes(5)) return TimeSpan.FromMinutes(2);
        if (age < TimeSpan.FromMinutes(10)) return TimeSpan.FromMinutes(5);
        if (age < TimeSpan.FromMinutes(20)) return TimeSpan.FromMinutes(10);
        if (age < TimeSpan.FromMinutes(40)) return TimeSpan.FromMinutes(20);
        if (age < TimeSpan.FromMinutes(80)) return TimeSpan.FromMinutes(40);
        return TimeSpan.FromMinutes(60);
    }

    /// <summary>
    /// Maps a record parse failure to a short stable category for the Hangfire state reason. Unknown
    /// texts fall back to <see cref="MalformedRecordCategory"/>.
    /// </summary>
    internal static string CategorizeInvalidRecord(string detail) =>
        detail.StartsWith("Unsupported destination", StringComparison.Ordinal)
            ? UnsupportedDestinationCategory
            : MalformedRecordCategory;

    /// <summary>
    /// Hard guard keeping a state reason within <see cref="MaxStateReasonLength"/> characters, without
    /// splitting a surrogate pair.
    /// </summary>
    internal static string BoundStateReason(string reason)
    {
        if (reason.Length <= MaxStateReasonLength) return reason;

        var length = MaxStateReasonLength;
        if (char.IsHighSurrogate(reason[length - 1])) length--;
        return reason[..length];
    }

    private async Task<CandidateOutcome> ProcessCandidateAsync(string jobId, JobStorage storage,
        IBackgroundJobClient client, DateTime now, CancellationToken ct)
    {
        DateTime? createdAtUtc = null;

        try
        {
            StateData? state;
            JobData? jobData;
            using (var connection = storage.GetConnection())
            {
                state = connection.GetStateData(jobId);
                jobData = connection.GetJobData(jobId);
            }

            // Recovery only ever acts on jobs currently in Indexing; anything else (published,
            // cancelled, expired) is a stale member. Deleted jobs are never revived.
            if (jobData is null || state is null || state.Name != IndexingState.StateName)
            {
                _logger.LogDebug("Removing stale recovery member {JobId} (state {State})", jobId,
                    state?.Name ?? "<missing>");
                return RemoveStale(storage, jobId);
            }

            createdAtUtc = AsUtc(jobData.CreatedAt);

            var parsed = IndexedJobPublicationRecord.TryParse(state.Data);
            switch (parsed.Status)
            {
                case IndexedJobRecordParseStatus.NewerSchema:
                    // Written by a newer publisher (rolling upgrade): leave it for a host that can read it.
                    string? version = null;
                    state.Data?.TryGetValue(IndexedJobPublicationRecord.SchemaVersionKey, out version);
                    _logger.LogWarning(
                        "Indexed job {JobId} has publication record schema version {SchemaVersion}, newer than supported " +
                        "{SupportedVersion}; deferring: {Reason}", jobId, version ?? "<unknown>",
                        IndexedJobPublicationRecord.CurrentSchemaVersion, parsed.Reason);
                    return Defer(storage, jobId, now, createdAtUtc);

                case IndexedJobRecordParseStatus.Invalid:
                    var detail = parsed.Reason ?? "Unknown reason";
                    return await CancelInvalidAsync(jobId, CategorizeInvalidRecord(detail), detail, touchRow: true,
                        storage, client, now, createdAtUtc, ct);
            }

            var record = parsed.Record!;
            createdAtUtc = record.CreatedAtUtc;

            // A job whose method cannot be loaded here (e.g. a host running an older build) would be
            // moved to Failed by the transition; leave it untouched for a host that can load it.
            if (jobData.LoadException != null || jobData.Job == null)
            {
                _logger.LogWarning(jobData.LoadException,
                    "Indexed job {JobId} cannot be loaded on this host; deferring its publication", jobId);
                return Defer(storage, jobId, now, createdAtUtc);
            }

            var result = await IndexedJobPublication.PublishAsync(jobId, record, storage, client, _writer,
                jobData.Job.ToString(), _logger, ct);

            switch (result.Outcome)
            {
                case IndexedJobPublicationOutcome.Published:
                    // Unapply removed the member in the same transaction; this is a defensive no-op.
                    TryRemoveMember(storage, jobId);
                    _logger.LogInformation("Recovered publication of indexed job {JobId} ({Destination})", jobId,
                        record.Destination);
                    return CandidateOutcome.Published;

                case IndexedJobPublicationOutcome.AlreadyLeftIndexing:
                    return RemoveStale(storage, jobId);

                case IndexedJobPublicationOutcome.Incompatible:
                    // The JobIndex row of this publication has another identity: the row is not ours to modify.
                    return await CancelInvalidAsync(jobId, IncompatibleIdentityCategory,
                        "JobIndex row has an incompatible identity", touchRow: false, storage, client, now,
                        createdAtUtc, ct);

                default:
                    return Defer(storage, jobId, now, createdAtUtc);
            }
        }
        catch (Exception ex)
        {
            // Anything thrown here (storage or database outage, cancellation) is transient: never
            // classify it as an invalid record, keep the member and retry later.
            _logger.LogWarning(ex, "Recovery of indexed job {JobId} failed; deferring", jobId);
            return Defer(storage, jobId, now, createdAtUtc);
        }
    }

    /// <summary>
    /// Cancels a job whose publication record cannot be used. The job and its state data stay in
    /// storage as evidence; only a guarded transition out of <c>Indexing</c> is attempted. The Hangfire
    /// state reason carries only the short <paramref name="category"/>; the full <paramref name="detail"/>
    /// (which may embed parser messages of any length) is logged with the job id.
    /// </summary>
    private async Task<CandidateOutcome> CancelInvalidAsync(string jobId, string category, string detail,
        bool touchRow, JobStorage storage, IBackgroundJobClient client, DateTime now, DateTime? createdAtUtc,
        CancellationToken ct)
    {
        _logger.LogError(
            "Indexed job {JobId} has an invalid publication record ({Category}) and will be cancelled: {Reason}",
            jobId, category, detail);

        bool cancelled;
        try
        {
            var stateReason = BoundStateReason($"{InvalidRecordReasonPrefix}: {category}");
            cancelled = client.ChangeState(jobId, new DeletedState { Reason = stateReason },
                IndexingState.StateName);
        }
        catch (Exception ex)
        {
            // A storage outage never deletes: keep the member and try again later.
            _logger.LogWarning(ex, "Cannot cancel indexed job {JobId}; deferring", jobId);
            return Defer(storage, jobId, now, createdAtUtc);
        }

        if (!cancelled)
        {
            string? actual;
            using (var connection = storage.GetConnection())
                actual = connection.GetStateData(jobId)?.Name;

            if (actual == IndexingState.StateName)
                return Defer(storage, jobId, now, createdAtUtc);

            _logger.LogInformation("Indexed job {JobId} left {Indexing} before it could be cancelled (now {State})",
                jobId, IndexingState.StateName, actual ?? "<missing>");
            return RemoveStale(storage, jobId);
        }

        if (touchRow)
        {
            try
            {
                // No-ops when the publication never created its JobIndex row.
                await _writer.UpdateStateAsync(jobId, DeletedState.StateName, now, ct);
                await _writer.UpdateErrorAsync(jobId, InvalidRecordErrorType, ct);
            }
            catch (Exception ex)
            {
                // The job is already Deleted; state sync reconciles the row state, the error type may be lost.
                _logger.LogError(ex, "Indexed job {JobId} was cancelled but its JobIndex row could not be marked {ErrorType}",
                    jobId, InvalidRecordErrorType);
            }
        }

        // Unapply removed the member with the transition; this is a defensive no-op.
        TryRemoveMember(storage, jobId);
        return CandidateOutcome.Cancelled;
    }

    private CandidateOutcome Defer(JobStorage storage, string jobId, DateTime now, DateTime? createdAtUtc)
    {
        var age = createdAtUtc is { } created && now > created ? now - created : TimeSpan.Zero;
        var score = IndexingStateHandler.ToScore(now + RetryDelayFor(age));

        try
        {
            using var connection = storage.GetConnection();
            using var transaction = connection.CreateWriteTransaction();
            transaction.AddToSet(IndexingStateHandler.RecoverySetKey, jobId, score);
            transaction.Commit();
            return CandidateOutcome.Deferred;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot reschedule recovery of indexed job {JobId}; ending the pass", jobId);
            return CandidateOutcome.StorageFailed;
        }
    }

    /// <summary>Removes the member of a job that is no longer in <c>Indexing</c>.</summary>
    private CandidateOutcome RemoveStale(JobStorage storage, string jobId)
    {
        try
        {
            RemoveMember(storage, jobId);
            return CandidateOutcome.StaleRemoved;
        }
        catch (Exception ex)
        {
            // The member stays due and is removed by a later pass; a failing storage ends this one.
            _logger.LogWarning(ex, "Cannot remove stale recovery member {JobId}; ending the pass", jobId);
            return CandidateOutcome.StorageFailed;
        }
    }

    /// <summary>Best-effort removal of a member the leaving transition has normally removed already.</summary>
    private void TryRemoveMember(JobStorage storage, string jobId)
    {
        try
        {
            RemoveMember(storage, jobId);
        }
        catch (Exception ex)
        {
            // A leftover member is dropped as stale when it becomes due.
            _logger.LogDebug(ex, "Cannot remove recovery member {JobId}", jobId);
        }
    }

    private static void RemoveMember(JobStorage storage, string jobId)
    {
        using var connection = storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        transaction.RemoveFromSet(IndexingStateHandler.RecoverySetKey, jobId);
        transaction.Commit();
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
