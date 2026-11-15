using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    /// Takes the lock of <paramref name="resource"/> on a new connection (not pooled: closing it ends the session),
    /// waiting up to <paramref name="timeout"/>. The lock name is the digest of the database and the resource under
    /// <paramref name="keyPrefix"/>, so two Registry databases on one server never share a lock.
    /// </summary>
    /// <returns>The lease, which releases the lock and closes the connection when disposed; null when another session
    /// holds the lock past the wait.</returns>
    /// <exception cref="InvalidOperationException">MySQL could not evaluate the lock request (it answered NULL).</exception>
    internal static IDisposable? TryAcquire(string connectionString, string resource, string keyPrefix, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        var session = new MySqlConnection(new MySqlConnectionStringBuilder(connectionString)
        {
            Pooling = false
        }.ConnectionString);
        string? name = null;
        try
        {
            session.Open();
            name = DigestKey($"{session.Database.Length}:{session.Database}{resource}", keyPrefix);
            using var command = session.CreateCommand();
            command.CommandText = "SELECT GET_LOCK(@name, @seconds)";
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@seconds", (int)Math.Ceiling(timeout.TotalSeconds));
            var acquired = command.ExecuteScalar();
            if (acquired is null or DBNull)
                throw new InvalidOperationException("MySQL could not acquire the session lock " + keyPrefix);
            if (Convert.ToInt32(acquired, CultureInfo.InvariantCulture) == 1)
                return new Lease(session, name);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        session.Dispose();
        return null;
    }

    private sealed class Lease(MySqlConnection session, string name) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (session.State == System.Data.ConnectionState.Open)
                {
                    using var command = session.CreateCommand();
                    command.CommandText = "SELECT RELEASE_LOCK(@name)";
                    command.Parameters.AddWithValue("@name", name);
                    command.ExecuteScalar();
                }
            }
            catch (MySqlException) when (session.State != System.Data.ConnectionState.Open)
            {
                // The session died while the lock was held (a dropped network, a killed connection): the server
                // dropped the lock with it, so there is nothing left to release
            }
            finally
            {
                // Nonpooled disposal closes the server session even if RELEASE_LOCK fails.
                session.Dispose();
            }
        }
    }
}
