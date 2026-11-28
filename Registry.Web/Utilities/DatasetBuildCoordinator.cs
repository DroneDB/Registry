using System;
using Hangfire;
using Hangfire.MySql;
using Hangfire.Storage;

namespace Registry.Web.Utilities;

/// <summary>
/// The per-dataset distributed lock of the configured Hangfire storage (a MySQL session lock on top of the
/// storage lock with MySQL storage, the storage lock alone otherwise). The key prefix names what it serializes:
/// <see cref="PendingKeyPrefix"/> the pending-build snapshots, indexed enqueueing and retry-state updates,
/// <see cref="HeavySubmitKeyPrefix"/> the checks and enqueue of a heavy-task submit.
/// </summary>
internal static class DatasetBuildCoordinator
{
    /// <summary>Lock of the pending-build coordination.</summary>
    internal const string PendingKeyPrefix = "ddb-pending";

    /// <summary>Lock of a heavy-task submit (its checks and its enqueue).</summary>
    internal const string HeavySubmitKeyPrefix = "heavy-submit";

    internal static string ResourceKey(string orgSlug, string dsSlug, string keyPrefix = PendingKeyPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(dsSlug);
        return MySqlSessionLock.DigestKey($"{orgSlug.Length}:{orgSlug}{dsSlug.Length}:{dsSlug}", keyPrefix);
    }

    internal static string RetryStateKey(string orgSlug, string dsSlug) => ResourceKey(orgSlug, dsSlug) + ":retry";

    /// <summary>
    /// Takes the lock of the dataset for <paramref name="keyPrefix"/>, waiting up to 5 seconds.
    /// </summary>
    /// <returns>The lease; disposing it releases the lock.</returns>
    /// <exception cref="PendingBuildCoordinatorContentionException">Another holder kept the lock past the wait.</exception>
    internal static IDisposable Acquire(string orgSlug, string dsSlug, JobStorage storage,
        IStorageConnection? connection = null, string? mysqlConnectionString = null,
        string keyPrefix = PendingKeyPrefix)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var resource = ResourceKey(orgSlug, dsSlug, keyPrefix);
        IDisposable? session = null;
        IStorageConnection? ownedConnection = null;
        try
        {
            if (storage is MySqlStorage)
            {
                // This provider expires its row lock after the acquisition timeout,
                // without renewal. A session lock prevents expiry from allowing an
                // overlapping coordinator during slow synchronous index publication.
                if (string.IsNullOrWhiteSpace(mysqlConnectionString))
                    throw new InvalidOperationException("Pending coordination requires the configured Hangfire MySQL connection string");
                session = MySqlSessionLock.TryAcquire(mysqlConnectionString, resource, keyPrefix, TimeSpan.FromSeconds(5))
                    ?? throw new PendingBuildCoordinatorContentionException(orgSlug, dsSlug,
                        new DistributedLockTimeoutException(resource));
            }

            ownedConnection = connection == null ? storage.GetConnection() : null;
            var handle = (connection ?? ownedConnection!).AcquireDistributedLock(resource, TimeSpan.FromSeconds(5));
            return new Lease(handle, ownedConnection, session);
        }
        catch (Exception ex) when (ex is DistributedLockTimeoutException or MySqlDistributedLockException)
        {
            try { ownedConnection?.Dispose(); }
            finally { session?.Dispose(); }
            throw new PendingBuildCoordinatorContentionException(orgSlug, dsSlug, ex);
        }
        catch
        {
            try { ownedConnection?.Dispose(); }
            finally { session?.Dispose(); }
            throw;
        }
    }

    private sealed class Lease(IDisposable handle, IStorageConnection? ownedConnection, IDisposable? session) : IDisposable
    {
        public void Dispose()
        {
            try { handle.Dispose(); }
            finally
            {
                try { ownedConnection?.Dispose(); }
                finally { session?.Dispose(); }
            }
        }
    }
}
