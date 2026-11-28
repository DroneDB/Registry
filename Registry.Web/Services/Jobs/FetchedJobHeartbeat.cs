using System;
using System.Data.Common;
using System.Threading;
using Hangfire.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Registry.Web.Hosting;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// Keeps the queue row of a job fresh for as long as the job performs, with the MySQL storage. That storage hands
/// a queue row to another worker when it was fetched longer ago than the invisibility timeout, whether or not the
/// first worker still runs the job, so a job that outlasts the timeout (a task on a remote node can run for
/// many hours) would be performed a second time. The heartbeat writes the fetch time of the job's own row anew
/// every <see cref="DefaultInterval"/>, so the timeout only has to be longer than the heartbeat: a worker that dies
/// stops it, and the job is taken again one timeout after the last beat.
/// </summary>
public sealed class FetchedJobHeartbeat : IServerFilter
{
    /// <summary>How often a running job renews its queue row.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    private const string ItemKey = "FetchedJobHeartbeatTimer";

    private readonly IConfiguration _configuration;
    private readonly ILogger<FetchedJobHeartbeat> _log;
    private readonly TimeSpan _interval;
    private readonly string _queueTable;

    public FetchedJobHeartbeat(IConfiguration configuration, ILogger<FetchedJobHeartbeat> log)
        : this(configuration, log, DefaultInterval, RegistryJobsServiceCollectionExtensions.MySqlTablesPrefix)
    {
    }

    internal FetchedJobHeartbeat(IConfiguration configuration, ILogger<FetchedJobHeartbeat> log, TimeSpan interval,
        string tablesPrefix)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _interval = interval;
        _queueTable = "`" + (tablesPrefix + "JobQueue").Replace("`", "``") + "`";
    }

    public void OnPerforming(PerformingContext context)
    {
        var connectionString = HangfireMySql.ConnectionString(context.Storage, _configuration);
        if (connectionString is null)
            return;

        var jobId = context.BackgroundJob.Id;
        context.Items[ItemKey] = new Timer(_ => Beat(connectionString, jobId), null, _interval, _interval);
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var timer) && timer is IDisposable disposable)
            disposable.Dispose();
    }

    // Only the row of this job that a worker holds (a fetch token): a retry of the job queues a new row, which a
    // beat that was still running when the job ended must not mark as fetched
    private void Beat(string connectionString, string jobId)
    {
        try
        {
            using var connection = new MySqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {_queueTable} SET FetchedAt = UTC_TIMESTAMP() WHERE JobId = @jobId AND FetchToken IS NOT NULL";
            command.Parameters.AddWithValue("@jobId", jobId);
            command.ExecuteNonQuery();
        }
        catch (DbException ex)
        {
            // The next beat tries again; the timeout is many beats long
            _log.LogWarning(ex, "Cannot renew the queue row of job {JobId}", jobId);
        }
    }
}
