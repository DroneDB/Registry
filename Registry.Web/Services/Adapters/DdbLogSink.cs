#nullable enable
using System;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Writes libddb's log records and GDAL's messages to the host's logger (Serilog), category
/// <c>DroneDB</c>. Called on native threads: it never throws. A record of a running job's
/// operation (<see cref="JobOperations"/>) carries the job id and dataset, and its warnings and
/// errors are queued for the job's console; a record of a request's operation (<see cref="RequestOperations"/>)
/// carries the request id as <c>RequestId</c>, the property Serilog puts on the request's own records.
/// </summary>
public sealed class DdbLogSink : IDdbLogSink
{
    /// <summary>The logger category of the native records.</summary>
    public const string Category = "DroneDB";

    private readonly ILogger _logger;

    public DdbLogSink(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger(Category);
    }

    /// <summary>The lowest libddb level the logger writes: libddb does not even produce the records below it.</summary>
    public DdbLogLevel MinimumLevel
    {
        get
        {
            for (var level = DdbLogLevel.Trace; level < DdbLogLevel.Fatal; level++)
                if (_logger.IsEnabled(ToLogLevel(level)))
                    return level;
            return DdbLogLevel.Fatal;
        }
    }

    public void Write(DdbLogLevel level, string message, string source, ulong operationId)
    {
        try
        {
            var logLevel = ToLogLevel(level);
            if (!_logger.IsEnabled(logLevel))
                return;

            var job = JobOperations.Find(operationId);
            if (job != null)
            {
                _logger.Log(logLevel, "[{DdbSource}] {DdbMessage} (job {JobId}, dataset {Dataset})", source, message,
                    job.JobId, job.Dataset);
                if (logLevel >= LogLevel.Warning)
                    job.QueueNativeLine($"[{source}] {message}");
            }
            else if (operationId == 0)
                _logger.Log(logLevel, "[{DdbSource}] {DdbMessage}", source, message);
            else if (RequestOperations.Find(operationId) is { } requestId)
                _logger.Log(logLevel, "[{DdbSource}] {DdbMessage} (request {RequestId})", source, message, requestId);
            else
                _logger.Log(logLevel, "[{DdbSource}] {DdbMessage} (operation {DdbOperationId})", source, message,
                    operationId);
        }
        catch (Exception)
        {
            // A sink failure must not reach native code; the record is dropped
        }
    }

    public static LogLevel ToLogLevel(DdbLogLevel level) => level switch
    {
        DdbLogLevel.Trace => LogLevel.Trace,
        DdbLogLevel.Debug => LogLevel.Debug,
        DdbLogLevel.Info => LogLevel.Information,
        DdbLogLevel.Warning => LogLevel.Warning,
        DdbLogLevel.Error => LogLevel.Error,
        _ => LogLevel.Critical
    };
}
