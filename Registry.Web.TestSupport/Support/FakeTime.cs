using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Test.Support;

/// <summary>
/// Controllable clock for code that takes a <see cref="TimeProvider"/>: wall time and the
/// monotonic timestamp advance together, and only when the test calls <see cref="Advance"/>.
/// Timers (<see cref="CreateTimer"/>, so also <c>new CancellationTokenSource(delay, time)</c> and
/// the delay and timeout overloads of <c>Task</c> that take a provider) fire on the thread that
/// advances the clock, in due order, each at its own due time. <see cref="WhenTimerArmed"/> tells
/// a test that the code under test has reached the point where it starts waiting, without
/// guessing how long that takes.
/// </summary>
public sealed class FakeTime : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private readonly List<(TimeSpan DueTime, TaskCompletionSource Armed)> _armWaiters = [];
    private DateTime _now;
    private long _timestamp;

    public FakeTime(DateTime utcNow) => _now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    public DateTime Now
    {
        get { lock (_gate) return _now; }
    }

    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);

    public override long GetTimestamp()
    {
        lock (_gate) return _timestamp;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Moves the clock forward, firing every timer that falls due on the way.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        long target;
        lock (_gate) target = _timestamp + by.Ticks;

        while (true)
        {
            FakeTimer? due;
            lock (_gate)
            {
                due = _timers.Where(t => t.DueAt <= target).MinBy(t => t.DueAt);
                if (due == null)
                {
                    Move(target - _timestamp);
                    return;
                }

                // Each timer sees the clock at its own due time
                Move(Math.Max(0, due.DueAt - _timestamp));
                if (due.Period > TimeSpan.Zero)
                    due.DueAt += due.Period.Ticks;
                else
                    _timers.Remove(due);
            }

            due.Fire(); // outside the lock: the callback may create or change timers
        }
    }

    private void Move(long ticks)
    {
        _now += TimeSpan.FromTicks(ticks);
        _timestamp += ticks;
    }

    /// <summary>Moves wall time (not the monotonic timestamp, nor the timers).</summary>
    public void Set(DateTime utcNow)
    {
        lock (_gate) _now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
    }

    /// <summary>
    /// Completes when a timer with exactly <paramref name="dueTime"/> is armed (created or changed)
    /// after this call. Ask before triggering the code that arms it.
    /// </summary>
    public Task WhenTimerArmed(TimeSpan dueTime)
    {
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _armWaiters.Add((dueTime, armed));
        return armed.Task;
    }

    /// <summary>Timers that are armed and have not fired (or have a period).</summary>
    public int ArmedTimers
    {
        get { lock (_gate) return _timers.Count; }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>A <see cref="Microsoft.Extensions.Caching.Memory.MemoryCacheOptions.Clock"/> reading this clock.</summary>
#pragma warning disable CS0618 // MemoryCacheOptions.Clock still takes ISystemClock
    public Microsoft.Extensions.Internal.ISystemClock SystemClock => new SystemClockAdapter(this);

    private sealed class SystemClockAdapter(FakeTime time) : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
#pragma warning restore CS0618

    /// <summary>Rounds up to a whole minute (set scores are whole minutes since a fixed epoch).</summary>
    public static DateTime CeilMinute(DateTime value) =>
        new(((value.Ticks + TimeSpan.TicksPerMinute - 1) / TimeSpan.TicksPerMinute) * TimeSpan.TicksPerMinute,
            DateTimeKind.Utc);

    private sealed class FakeTimer(FakeTime time, TimerCallback callback, object? state) : ITimer
    {
        public long DueAt;
        public TimeSpan Period;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            List<TaskCompletionSource> armed = [];
            lock (time._gate)
            {
                time._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return true;

                ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, TimeSpan.Zero);
                DueAt = time._timestamp + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                time._timers.Add(this);

                for (var i = time._armWaiters.Count - 1; i >= 0; i--)
                {
                    if (time._armWaiters[i].DueTime != dueTime) continue;
                    armed.Add(time._armWaiters[i].Armed);
                    time._armWaiters.RemoveAt(i);
                }
            }

            foreach (var waiter in armed) waiter.TrySetResult();
            return true;
        }

        public void Dispose()
        {
            lock (time._gate) time._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
