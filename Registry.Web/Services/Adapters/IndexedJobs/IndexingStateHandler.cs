#nullable enable
using System;
using System.Linq;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Keeps every job in the <c>Indexing</c> state discoverable by recovery. Hangfire runs state handlers
/// inside the state transaction, so the recovery-set member is committed atomically with the state
/// that needs it and removed by the transaction that leaves <c>Indexing</c> (the same mechanism the
/// built-in scheduled state uses for its schedule set).
/// </summary>
public sealed class IndexingStateHandler : IStateHandler
{
    /// <summary>Hangfire set holding the ids of jobs whose publication may need recovery.</summary>
    public const string RecoverySetKey = "registry:indexed-job-publication-recovery";

    /// <summary>Time a publisher gets to finish on its own before recovery considers the job.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Scores are whole minutes since this epoch: MySQL stores set scores as single-precision FLOAT,
    /// which represents whole numbers exactly up to 2^24 (about 31 years of minutes).
    /// </summary>
    public static readonly DateTime ScoreEpochUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly object RegistrationLock = new();

    public string StateName => IndexingState.StateName;

    public void Apply(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        var parsed = IndexedJobPublicationRecord.TryParse(context.NewState.SerializeData());

        // An unusable record becomes due immediately so recovery classifies it promptly.
        var score = parsed.Record is { } record
            ? ToScore(record.CreatedAtUtc + GracePeriod)
            : ScoreForNow(DateTime.UtcNow);

        transaction.AddToSet(RecoverySetKey, context.BackgroundJob.Id, score);
    }

    public void Unapply(ApplyStateContext context, IWriteOnlyTransaction transaction) =>
        transaction.RemoveFromSet(RecoverySetKey, context.BackgroundJob.Id);

    /// <summary>Score of a member due at <paramref name="dueUtc"/>: rounded up so it is never due early.</summary>
    public static double ToScore(DateTime dueUtc) =>
        Math.Max(0, Math.Ceiling((ToUtc(dueUtc) - ScoreEpochUtc).TotalMinutes));

    /// <summary>
    /// Upper bound for due members at <paramref name="nowUtc"/>: rounded down, so a member rescheduled
    /// with <see cref="ToScore"/>(now + delay) is never read again in the same pass.
    /// </summary>
    public static double ScoreForNow(DateTime nowUtc) =>
        Math.Max(0, Math.Floor((ToUtc(nowUtc) - ScoreEpochUtc).TotalMinutes));

    /// <summary>True when the handler is registered in <see cref="GlobalStateHandlers"/>.</summary>
    public static bool IsRegistered()
    {
        lock (RegistrationLock)
            return GlobalStateHandlers.Handlers.OfType<IndexingStateHandler>().Any();
    }

    /// <summary>Registers the handler in <see cref="GlobalStateHandlers"/> once; repeated calls are no-ops.</summary>
    public static void EnsureRegistered()
    {
        lock (RegistrationLock)
        {
            if (!GlobalStateHandlers.Handlers.OfType<IndexingStateHandler>().Any())
                GlobalStateHandlers.Handlers.Add(new IndexingStateHandler());
        }
    }

    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
