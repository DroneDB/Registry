using System;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Registry.Web.Utilities;

/// <summary>
/// A MySQL session lock (<c>GET_LOCK</c>): held by a connection of its own, so it lasts as long as that
/// connection does and the server drops it when the process dies, however it dies. Unlike the lock rows of the
/// Hangfire MySQL storage, which expire after the acquisition timeout without renewal, it cannot lapse while the
/// holder still works.
/// </summary>
internal static class MySqlSessionLock
{
    /// <summary>
    /// Names a lock <c>prefix:hash</c>, the hash being that of <paramref name="identity"/>: MySQL limits a lock name to
    /// 64 characters, which the prefix, the ':' and the 43 characters of the hash stay within.
    /// </summary>
    internal static string DigestKey(string identity, string keyPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return keyPrefix + ":" + hash;
    }

    /// <summary>
    /// How long the server lets the session of a lease sit idle (<c>wait_timeout</c>): the longest its platforms accept
    /// (24 days on Windows). The session sends nothing while the run it protects works, and a server that ends an idle
    /// session ends its locks with it, so the default (8 hours, often set far lower) would free the lock mid-run.
    /// </summary>
    internal const int SessionIdleTimeoutSeconds = 2147483;

    /// <summary>
    /// Takes the lock of <paramref name="resource"/> on a new connection (not pooled: closing it ends the session),
    /// waiting up to <paramref name="timeout"/>. The lock name is the digest of the database and the resource under
    /// <paramref name="keyPrefix"/>, so two Registry databases on one server never share a lock. The session lives as
    /// long as the lease: its idle timeout is raised (<see cref="SessionIdleTimeoutSeconds"/>) and the connection sends TCP
    /// keep-alive probes, so neither the server nor a proxy between drops it while the lease is held.
    /// </summary>
    /// <param name="log">Where a failure to release the lock is logged (the lease never throws it); null: not logged.</param>
    /// <returns>The lease, which releases the lock and closes the connection when disposed; null when another session
    /// holds the lock past the wait.</returns>
    /// <exception cref="InvalidOperationException">MySQL could not evaluate the lock request (it answered NULL).</exception>
    internal static IDisposable? TryAcquire(string connectionString, string resource, string keyPrefix, TimeSpan timeout,
        ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        var session = new MySqlConnection(new MySqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Keepalive = 30
        }.ConnectionString);
        try
        {
            session.Open();
            using (var idle = session.CreateCommand())
            {
                idle.CommandText = "SET SESSION wait_timeout = " + SessionIdleTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
                idle.ExecuteNonQuery();
            }

            var name = DigestKey($"{session.Database.Length}:{session.Database}{resource}", keyPrefix);
            using var command = session.CreateCommand();
            command.CommandText = "SELECT GET_LOCK(@name, @seconds)";
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@seconds", (int)Math.Ceiling(timeout.TotalSeconds));
            var acquired = command.ExecuteScalar();
            if (acquired is null or DBNull)
                throw new InvalidOperationException("MySQL could not acquire the session lock " + keyPrefix);
            if (Convert.ToInt32(acquired, CultureInfo.InvariantCulture) == 1)
                return new Lease(session, name, log);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        session.Dispose();
        return null;
    }

    /// <summary>
    /// A lock held by a session. Disposing it never throws: the run it protected has ended (its body may have
    /// succeeded), and closing the session frees the lock on the server whether or not the explicit release worked.
    /// </summary>
    internal class Lease(MySqlConnection session, string name, ILogger? log) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (session.State == System.Data.ConnectionState.Open)
                    Release(session, name);
            }
            // A session that died took the lock with it (a dropped network, a killed connection); one that is open and
            // failed to release it frees it when it closes just below
            catch (DbException ex)
            {
                Failed(ex, "release");
            }
            catch (InvalidOperationException ex)
            {
                Failed(ex, "release");
            }
            finally
            {
                // Nonpooled disposal closes the server session even if RELEASE_LOCK fails.
                try { session.Dispose(); }
                catch (DbException ex) { Failed(ex, "close the session of"); }
                catch (InvalidOperationException ex) { Failed(ex, "close the session of"); }
            }
        }

        private void Failed(Exception ex, string what) =>
            log?.LogWarning(ex, "Cannot {What} the lock {Lock}; closing its session frees it", what, name);

        /// <summary>Releases the lock on its session (a seam for the test that makes it fail).</summary>
        protected virtual void Release(MySqlConnection connection, string lockName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT RELEASE_LOCK(@name)";
            command.Parameters.AddWithValue("@name", lockName);
            command.ExecuteScalar();
        }
    }
}
