using System;
using Hangfire;
using Hangfire.MySql;
using Microsoft.Extensions.Configuration;
using Registry.Web.Exceptions;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// The per-dataset lock of a heavy-task submit (FR-XC-25): the dedup lookup, the active-output check, the quota and
/// the enqueue of one submit run while no other submit of the same dataset does, on every web host, so two clicks
/// cannot both pass the checks and start two tasks. It is the distributed lock of
/// <see cref="DatasetBuildCoordinator"/> under its own key (<see cref="DatasetBuildCoordinator.HeavySubmitKeyPrefix"/>).
/// </summary>
public class HeavyTaskSubmitLock
{
    /// <summary>Seconds advertised in <c>Retry-After</c> when the lock stays busy.</summary>
    public const int RetryAfterSeconds = 2;

    /// <summary>The detail of the 503 answered when the lock stays busy.</summary>
    public const string BusyMessage =
        "Another task is being started in this dataset. Try again in a moment.";

    private readonly JobStorage _storage;
    private readonly string? _mysqlConnectionString;

    public HeavyTaskSubmitLock(JobStorage storage, IConfiguration? configuration = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _mysqlConnectionString = storage is MySqlStorage
            ? configuration?.GetConnectionString(MagicStrings.HangfireConnectionName)
            : null;
    }

    /// <summary>
    /// Takes the submit lock of the dataset, waiting a few seconds for another submit to end.
    /// </summary>
    /// <returns>The lease; disposing it releases the lock.</returns>
    /// <exception cref="TransientException">Another submit kept the lock past the wait (503 with <c>Retry-After</c>).</exception>
    public virtual IDisposable Acquire(string orgSlug, string dsSlug)
    {
        try
        {
            return DatasetBuildCoordinator.Acquire(orgSlug, dsSlug, _storage, null, _mysqlConnectionString,
                DatasetBuildCoordinator.HeavySubmitKeyPrefix);
        }
        catch (PendingBuildCoordinatorContentionException ex)
        {
            throw new TransientException(BusyMessage, ex, RetryAfterSeconds);
        }
    }
}
