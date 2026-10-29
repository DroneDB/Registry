using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Registry.Web.Filters;
using Registry.Web.Services.HeavyTasks.Adapters;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// The output of a background job, written once to every channel: the server log (a constant
/// message template with its arguments, so each value stays a property) and, while the job runs
/// on a Hangfire server, the job console and the build log tail (<see cref="LogRingBuffer"/>,
/// when <see cref="BuildLogCaptureFilter"/> attached one). Console and tail lines are rendered,
/// prefixed with their level when it is a warning or an error, and scrubbed of the dataset
/// folder when one is given (<see cref="JobLogText.ScrubDatasetPath"/>). Native warnings queued
/// for the job (<see cref="JobOperations"/>) are written first, by the job's thread. Thread-safe.
/// </summary>
public sealed class JobConsole
{
    private readonly ILogger _logger;
    private readonly PerformContext? _context;
    private readonly LogRingBuffer? _tail;
    private readonly string? _scrubFolder;
    private readonly JobOperation? _operation;
    private readonly Lock _gate = new();

    private JobConsole(ILogger logger, PerformContext? context, string? scrubFolder)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _context = context;
        _scrubFolder = scrubFolder;
        _tail = context != null && context.Items.TryGetValue(BuildLogCaptureFilter.BufferKey, out var raw)
            ? raw as LogRingBuffer
            : null;
        _operation = JobOperations.Current;
    }

    /// <summary>The console of the job running with <paramref name="context"/> (null outside a Hangfire server).</summary>
    /// <param name="logger">The server log of the job's class.</param>
    /// <param name="context">The job's context, or null when the code runs outside a job.</param>
    /// <param name="scrubFolder">A dataset folder to remove from the console and tail lines.</param>
    public static JobConsole For(ILogger logger, PerformContext? context, string? scrubFolder = null) =>
        new(logger, context, scrubFolder);

    /// <summary>A progress line: the server log gets it at debug level.</summary>
    public void Debug(string template, params object?[] args) => Write(LogLevel.Debug, null, template, args);

    public void Info(string template, params object?[] args) => Write(LogLevel.Information, null, template, args);

    public void Warning(string template, params object?[] args) => Write(LogLevel.Warning, null, template, args);

    public void Warning(Exception? exception, string template, params object?[] args) =>
        Write(LogLevel.Warning, exception, template, args);

    public void Error(Exception? exception, string template, params object?[] args) =>
        Write(LogLevel.Error, exception, template, args);

    private void Write(LogLevel level, Exception? exception, string template, object?[] args)
    {
        var values = new LogValues(template, args);
        _logger.Log(level, default, values, exception, static (state, _) => state.ToString());

        if (_context == null)
            return;

        var line = level switch
        {
            LogLevel.Warning => "WARNING: " + values,
            >= LogLevel.Error => "ERROR: " + values,
            _ => values.ToString()
        };

        lock (_gate)
        {
            if (_operation != null)
                foreach (var native in _operation.TakeNativeLines())
                    Emit(native);
            Emit(line);
        }
    }

    // Under _gate: the console shares the job's storage connection
    private void Emit(string line)
    {
        var text = JobLogText.ScrubDatasetPath(line, _scrubFolder);
        _context!.WriteLine(text);
        if (_tail != null)
            lock (_tail)
                _tail.Append(text);
    }

    /// <summary>
    /// The logger state of a line: its named arguments and the template under
    /// <c>{OriginalFormat}</c>, as Microsoft.Extensions.Logging's own state carries them, so the
    /// logging providers (Serilog) keep it structured.
    /// </summary>
    private sealed class LogValues(string template, object?[] args) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly IReadOnlyList<string> _names = MessageTemplate.HoleNames(template);
        private string? _rendered;

        public int Count => Math.Min(_names.Count, args.Length) + 1;

        public KeyValuePair<string, object?> this[int index] => index == Count - 1
            ? new KeyValuePair<string, object?>("{OriginalFormat}", template)
            : new KeyValuePair<string, object?>(_names[index], args[index]);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => _rendered ??= MessageTemplate.Render(template, args);
    }
}
