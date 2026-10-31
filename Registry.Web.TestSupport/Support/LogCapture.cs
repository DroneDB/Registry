using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Registry.Web.Test.Support;

/// <summary>One recorded log call.</summary>
public sealed record LogRecord(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Records what a <see cref="RegistryWebFactory"/> host logs, so a test can assert on levels, messages and
/// whether a stack (an exception) was attached. The host's Serilog logger reads its sinks from the
/// container (<c>ReadFrom.Services</c>): register it with <c>services.AddSingleton&lt;ILogEventSink&gt;(capture)</c>
/// in the factory's service configuration. It records the events the configured minimum level lets through.
/// </summary>
public sealed class LogCapture : ILogEventSink
{
    private readonly ConcurrentQueue<LogRecord> _records = new();

    public IReadOnlyList<LogRecord> Records => _records.ToArray();

    /// <summary>The records at <paramref name="level"/> or above.</summary>
    public IReadOnlyList<LogRecord> AtLeast(LogLevel level) => Records.Where(r => r.Level >= level).ToArray();

    public void Clear() => _records.Clear();

    public void Emit(LogEvent logEvent)
    {
        var category = logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var source)
            ? source.ToString().Trim('"')
            : string.Empty;
        _records.Enqueue(new LogRecord(category, ToLogLevel(logEvent.Level), logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture),
            logEvent.Exception));
    }

    private static LogLevel ToLogLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        _ => LogLevel.Critical
    };
}
