#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Bridges tool-emitted <see cref="HeavyToolProgress"/> to the three persistence channels: the
/// Hangfire console (permanent operator log), the <see cref="LogRingBuffer"/> tail (what
/// TaskHistory shows), and throttled <c>JobIndex</c> progress/phase updates.
/// </summary>
/// <remarks>
/// A tool reports from any thread (native progress callbacks run on worker threads), so
/// <see cref="Report"/> only updates the tail and takes a snapshot under a lock. One consumer task
/// owns the slow side: it writes the console lines and the latest snapshot, one write at a time,
/// through an <see cref="IJobIndexWriter"/> of the sink's own DI scope (never the task's scoped
/// DbContext, which is not thread-safe). No reporting thread waits for the database.
/// <see cref="CompleteAsync"/> drains it and writes the final tail.
/// </remarks>
public sealed class HangfireProgressSink : IProgress<HeavyToolProgress>, IAsyncDisposable
{
    private readonly PerformContext? _ctx;
    private readonly LogRingBuffer _logBuffer;
    private readonly string _taskId;
    private readonly TimeSpan _throttle;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Queue<string> _consoleLines = new();
    // Capacity 1, latest wins: a signal that a snapshot is waiting, never a backlog
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly IServiceScope _scope;
    private readonly IJobIndexWriter _writer;
    private readonly Task _consumer;

    private DateTime _lastFlushUtc = DateTime.MinValue;
    private int? _lastPercent;
    private string? _lastPhase;
    private Snapshot? _pending;
    private int _completed;

    private sealed record Snapshot(int? Percent, string? Phase, string LogTailJson);

    public HangfireProgressSink(PerformContext? ctx, IServiceScopeFactory scopes, LogRingBuffer logBuffer,
        string taskId, int throttleSeconds, ILogger<HangfireProgressSink> log)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _ctx = ctx;
        _logBuffer = logBuffer ?? throw new ArgumentNullException(nameof(logBuffer));
        _taskId = taskId;
        _throttle = TimeSpan.FromSeconds(Math.Max(0, throttleSeconds));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _scope = scopes.CreateScope();
        _writer = _scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
        _consumer = Task.Run(ConsumeAsync);
    }

    public void Report(HeavyToolProgress value)
    {
        if (value is null) return;

        int? percent = value.Fraction < 0
            ? null
            : (int)Math.Round(Math.Clamp(value.Fraction, 0, 1) * 100);

        lock (_gate)
        {
            // Log text goes to the tail now and to the console through the consumer
            if (!string.IsNullOrEmpty(value.LogChunk))
            {
                foreach (var line in value.LogChunk!.Split('\n'))
                {
                    var trimmed = line.TrimEnd('\r');
                    if (trimmed.Length == 0) continue;
                    _logBuffer.Append(trimmed, "info", value.Phase);
                    _consoleLines.Enqueue(trimmed);
                }
            }
            else if (!string.IsNullOrEmpty(value.Message))
            {
                _logBuffer.Append(value.Message!, "info", value.Phase);
            }

            var now = DateTime.UtcNow;
            var phaseChanged = value.Phase is not null && value.Phase != _lastPhase;
            if (phaseChanged || now - _lastFlushUtc >= _throttle)
            {
                _lastFlushUtc = now;
                _lastPercent = percent;
                _lastPhase = value.Phase ?? _lastPhase;
                _pending = new Snapshot(_lastPercent, _lastPhase, _logBuffer.ToJson());
            }
            else if (_consoleLines.Count == 0)
            {
                return;
            }
        }

        _signal.Writer.TryWrite(true);
    }

    /// <summary>
    /// Waits for the writes in progress, then writes the final tail and phase. Idempotent; call it
    /// before the task's own JobIndex writes (artifact, error).
    /// </summary>
    public async Task CompleteAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        _signal.Writer.TryComplete();
        await _consumer;

        Snapshot final;
        lock (_gate)
            final = new Snapshot(_lastPercent, _lastPhase, _logBuffer.ToJson());
        await WriteAsync(final);
        _scope.Dispose();
    }

    public async ValueTask DisposeAsync() => await CompleteAsync();

    private async Task ConsumeAsync()
    {
        await foreach (var _ in _signal.Reader.ReadAllAsync())
            await DrainAsync();
        await DrainAsync();
    }

    private async Task DrainAsync()
    {
        Snapshot? snapshot;
        string[] lines;
        lock (_gate)
        {
            snapshot = _pending;
            _pending = null;
            lines = [.. _consoleLines];
            _consoleLines.Clear();
        }

        foreach (var line in lines)
        {
            try { _ctx?.WriteLine(line); }
            catch (Exception ex) { _log.LogDebug(ex, "Cannot write to the console of task {TaskId}", _taskId); }
        }

        if (snapshot != null)
            await WriteAsync(snapshot);
    }

    // Progress is telemetry: a failed write is logged and never fails the tool
    private async Task WriteAsync(Snapshot snapshot)
    {
        try
        {
            await _writer.UpdateProgressAsync(_taskId, snapshot.Percent, snapshot.Phase, snapshot.LogTailJson,
                DateTime.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cannot write the progress of task {TaskId}", _taskId);
        }
    }
}
