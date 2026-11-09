using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Hangfire.MySql;
using Hangfire.Storage;
using MySqlConnector;

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
        return DigestKey($"{orgSlug.Length}:{orgSlug}{dsSlug.Length}:{dsSlug}", keyPrefix);
    }

    internal static string RetryStateKey(string orgSlug, string dsSlug) => ResourceKey(orgSlug, dsSlug) + ":retry";

    private static string DigestKey(string identity, string keyPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        // MySQL session lock names are limited to 64 characters: the prefix, ':' and the 43 characters of the hash.
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return keyPrefix + ":" + hash;
    }

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
        var configuredStorage = storage;
        var resource = ResourceKey(orgSlug, dsSlug, keyPrefix);
        MySqlConnection? session = null;
        string? sessionResource = null;
        IStorageConnection? ownedConnection = null;
        try
        {
            if (configuredStorage is MySqlStorage)
            {
                // This provider expires its row lock after the acquisition timeout,
                // without renewal. A session lock prevents expiry from allowing an
                // overlapping coordinator during slow synchronous index publication.
                if (string.IsNullOrWhiteSpace(mysqlConnectionString))
                    throw new InvalidOperationException("Pending coordination requires the configured Hangfire MySQL connection string");
                session = new MySqlConnection(new MySqlConnectionStringBuilder(mysqlConnectionString)
                {
                    Pooling = false
                }.ConnectionString);
                session.Open();
                sessionResource = DigestKey($"{session.Database.Length}:{session.Database}{resource}", keyPrefix);
                using var command = session.CreateCommand();
                command.CommandText = "SELECT GET_LOCK(@resource, 5)";
                command.Parameters.AddWithValue("@resource", sessionResource);
                var acquired = command.ExecuteScalar();
                if (acquired is null or DBNull)
                    throw new InvalidOperationException("MySQL could not acquire the pending coordinator session lock");
                if (Convert.ToInt32(acquired, CultureInfo.InvariantCulture) != 1)
                    throw new PendingBuildCoordinatorContentionException(orgSlug, dsSlug,
                        new DistributedLockTimeoutException(resource));
            }

            ownedConnection = connection == null ? configuredStorage.GetConnection() : null;
            var handle = (connection ?? ownedConnection!).AcquireDistributedLock(resource, TimeSpan.FromSeconds(5));
            return new Lease(handle, ownedConnection, session, sessionResource);
        }
        catch (Exception ex) when (ex is DistributedLockTimeoutException or MySqlDistributedLockException)
        {
            try { ownedConnection?.Dispose(); }
            finally { ReleaseSession(session, sessionResource); }
            throw new PendingBuildCoordinatorContentionException(orgSlug, dsSlug, ex);
        }
        catch
        {
            try { ownedConnection?.Dispose(); }
            finally { ReleaseSession(session, sessionResource); }
            throw;
        }
    }

    private static void ReleaseSession(MySqlConnection? session, string? resource)
    {
        if (session == null) return;
        try
        {
            if (session.State == System.Data.ConnectionState.Open && resource != null)
            {
                using var command = session.CreateCommand();
                command.CommandText = "SELECT RELEASE_LOCK(@resource)";
                command.Parameters.AddWithValue("@resource", resource);
                command.ExecuteScalar();
            }
        }
        finally
        {
            // Nonpooled disposal closes the server session even if RELEASE_LOCK fails.
            session.Dispose();
        }
    }

    private sealed class Lease(IDisposable handle, IStorageConnection? ownedConnection,
        MySqlConnection? session, string? sessionResource) : IDisposable
    {
        public void Dispose()
        {
            try { handle.Dispose(); }
            finally
            {
                try { ownedConnection?.Dispose(); }
                finally { ReleaseSession(session, sessionResource); }
            }
        }
    }
}
