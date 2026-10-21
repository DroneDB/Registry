#nullable enable
using System;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Writes libddb's log records and GDAL's messages to the host's logger (Serilog), category
/// <c>DroneDB</c> (XI-08). Called on native threads: it never throws. The operation id maps to a
/// dataset, job and trace once RQ-09's JobConsole sets it (<c>DDBSetThreadOperation</c>).
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

            if (operationId == 0)
                _logger.Log(logLevel, "[{DdbSource}] {DdbMessage}", source, message);
            else
                _logger.Log(logLevel, "[{DdbSource}] {DdbMessage} (operation {DdbOperationId})", source, message,
                    operationId);
        }
        catch (Exception)
        {
            // A sink failure must not reach native code (XI-03); the record is dropped
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
