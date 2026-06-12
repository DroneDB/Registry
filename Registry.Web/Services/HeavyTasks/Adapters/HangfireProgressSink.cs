#nullable enable
using System;
using Hangfire.Console;
using Hangfire.Server;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Bridges tool-emitted <see cref="HeavyToolProgress"/> to the three persistence
/// channels (spec §6): the Hangfire console (permanent operator log), the
/// <see cref="LogRingBuffer"/> tail (what TaskHistory shows), and throttled
/// <c>JobIndex</c> progress/phase updates.
/// </summary>
public sealed class HangfireProgressSink : IProgress<HeavyToolProgress>
{
    private readonly PerformContext? _ctx;
    private readonly IJobIndexWriter _indexWriter;
    private readonly LogRingBuffer _logBuffer;
    private readonly string _taskId;
    private readonly TimeSpan _throttle;
    private readonly object _gate = new();

    private DateTime _lastFlushUtc = DateTime.MinValue;
    private int? _lastPercent;
    private string? _lastPhase;

    public HangfireProgressSink(
        PerformContext? ctx, IJobIndexWriter indexWriter, LogRingBuffer logBuffer,
        string taskId, int throttleSeconds)
    {
        _ctx = ctx;
        _indexWriter = indexWriter;
        _logBuffer = logBuffer;
        _taskId = taskId;
        _throttle = TimeSpan.FromSeconds(Math.Max(0, throttleSeconds));
    }

    public void Report(HeavyToolProgress value)
    {
        if (value is null) return;

        int? percent = value.Fraction < 0
            ? null
            : (int)Math.Round(Math.Clamp(value.Fraction, 0, 1) * 100);

        // Append any provided log text to both channels.
        if (!string.IsNullOrEmpty(value.LogChunk))
        {
            foreach (var line in value.LogChunk!.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0) continue;
                lock (_gate) _logBuffer.Append(trimmed, "info", value.Phase);
                _ctx?.WriteLine(trimmed);
            }
        }
        else if (!string.IsNullOrEmpty(value.Message))
        {
            lock (_gate) _logBuffer.Append(value.Message!, "info", value.Phase);
        }

        bool flush;
        string? logTailJson = null;
        int? flushPercent = null;
        string? flushPhase = null;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var phaseChanged = value.Phase is not null && value.Phase != _lastPhase;
            var dueByTime = now - _lastFlushUtc >= _throttle;
            flush = phaseChanged || dueByTime;
            if (flush)
            {
                _lastFlushUtc = now;
                _lastPercent = percent;
                _lastPhase = value.Phase ?? _lastPhase;
                flushPercent = _lastPercent;
                flushPhase = _lastPhase;
                logTailJson = _logBuffer.ToJson();
            }
        }

        if (!flush) return;

        // Fire-and-forget the DB write; progress is best-effort telemetry and must
        // never block or fail the tool execution.
        try
        {
            _indexWriter
                .UpdateProgressAsync(_taskId, flushPercent, flushPhase, logTailJson, DateTime.UtcNow)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // swallow - telemetry only
        }
    }

    /// <summary>Forces a final flush of the current log tail to the index.</summary>
    public void FlushFinal()
    {
        string logTailJson;
        int? percent;
        string? phase;
        lock (_gate)
        {
            logTailJson = _logBuffer.ToJson();
            percent = _lastPercent;
            phase = _lastPhase;
        }

        try
        {
            _indexWriter.UpdateProgressAsync(_taskId, percent, phase, logTailJson, DateTime.UtcNow)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // swallow
        }
    }
}
