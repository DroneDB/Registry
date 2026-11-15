using Hangfire;
using Hangfire.MySql;
using Microsoft.Extensions.Configuration;

namespace Registry.Web.Utilities;

/// <summary>What the code that works beside the Hangfire MySQL storage (session locks, the queue heartbeat) needs of it.</summary>
internal static class HangfireMySql
{
    /// <summary>
    /// The connection string of the Hangfire database when <paramref name="storage"/> is the MySQL storage; null with
    /// any other storage (nothing then needs a connection of its own) or when the configuration has none.
    /// </summary>
    internal static string? ConnectionString(JobStorage? storage, IConfiguration? configuration) =>
        storage is MySqlStorage ? configuration?.GetConnectionString(MagicStrings.HangfireConnectionName) : null;
}
