using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Registry.Adapters.DroneDB
{
    /// <summary>
    /// Provides synchronization for SQLite database access across threads
    /// </summary>
    public static class DbSyncManager
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _databaseLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        /// <summary>
        /// Gets a semaphore for the specified database path
        /// </summary>
        /// <param name="databasePath">Full path to the database file</param>
        /// <returns>A semaphore that should be used to synchronize access</returns>
        public static SemaphoreSlim GetDatabaseLock(string? databasePath)
        {
            // If path is null, use a default key
            var key = databasePath ?? "default_db_lock";
            return _databaseLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }
    }
}
