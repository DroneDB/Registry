#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.Caching;

/// <summary>
/// One computation per key at a time within the process: concurrent callers of a key share the computation
/// started by the first (single-flight), so a burst of cache misses computes a value once. The owner of the
/// cache detaches a flight when it invalidates the key: later callers start a new flight, and the detached one
/// is marked so its result, computed from the state before the invalidation, is not stored.
/// <para>
/// Cancellation (RQ-13): a caller's token ends that caller's wait only. The computation gets the flight's
/// <see cref="Flight.Token"/>, canceled when the last caller still waiting has cancelled (a render for clients
/// that all went away stops), never while one waits. An abandoned flight leaves the table at the moment it is
/// abandoned (one lock for joins and leaves), so a new caller starts a fresh flight and never joins a canceled
/// one. A caller without a cancelable token keeps its flight alive.
/// </para>
/// </summary>
/// <typeparam name="T">The value computed.</typeparam>
public sealed class SingleFlight<T>
{
    private readonly Dictionary<string, Flight> _inFlight = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>One computation in progress.</summary>
    public sealed class Flight
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // No timer and no wait handle: nothing to release, so it is never disposed (a late cancel stays safe)
        private readonly CancellationTokenSource _abandoned = new();
        private volatile bool _invalidated;

        internal Task<T> Completion => _completion.Task;

        /// <summary>The key was invalidated while the value was computed: do not store the value.</summary>
        public bool Invalidated => _invalidated;

        /// <summary>Canceled when every caller waiting for the flight has cancelled: the computation's token.</summary>
        public CancellationToken Token => _abandoned.Token;

        // Callers waiting that have not cancelled, and whether the computation ended (both under the owner's lock)
        internal int Waiters;
        internal bool Ended;

        internal void Invalidate() => _invalidated = true;
        internal void Abandon() => _abandoned.Cancel();
        internal void Complete(T value) => _completion.SetResult(value);
        internal void Fail(Exception error) => _completion.SetException(error);
    }

    /// <summary>
    /// Joins the computation of <paramref name="key"/>, or starts it with <paramref name="compute"/>. The
    /// computation is shared, so it does not take any caller's token: it takes the flight's
    /// <see cref="Flight.Token"/>, canceled once no caller waits any more.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="compute">Computes the value; called by the first caller only, with its flight.</param>
    /// <param name="ct">Ends the wait of this caller (and leaves the flight).</param>
    /// <returns>The value, or the computation's exception, for every caller of the flight.</returns>
    public async Task<T> RunAsync(string key, Func<Flight, Task<T>> compute, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(compute);

        Flight flight;
        bool lead;
        lock (_lock)
        {
            lead = !_inFlight.TryGetValue(key, out flight!);
            if (lead)
            {
                flight = new Flight();
                _inFlight[key] = flight;
            }

            flight.Waiters++;
        }

        // Started on the pool, not on this thread: a computation that blocks (a native render behind an async
        // signature) never holds the leader, which waits like the others and leaves when its token is canceled
        if (lead)
            _ = Task.Run(() => LeadAsync(key, flight, compute));

        try
        {
            return await flight.Completion.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !flight.Completion.IsCompleted)
        {
            // Left in the caller's own flow, not from a callback on the token: the wait ends the moment the token
            // is canceled (also when it was canceled already), and the leave cannot be skipped
            Leave(key, flight);
            throw;
        }
    }

    /// <summary>Detaches the flight of <paramref name="key"/>, if any, and marks it invalidated.</summary>
    public void Invalidate(string key)
    {
        Flight? flight;
        lock (_lock)
        {
            if (!_inFlight.Remove(key, out flight))
                return;
        }

        flight.Invalidate();
    }

    /// <summary>Detaches every flight whose key starts with <paramref name="prefix"/> and marks it invalidated.</summary>
    public void InvalidatePrefix(string prefix)
    {
        List<Flight> detached;
        lock (_lock)
        {
            var keys = _inFlight.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            detached = new List<Flight>(keys.Count);
            foreach (var key in keys)
                if (_inFlight.Remove(key, out var flight))
                    detached.Add(flight);
        }

        foreach (var flight in detached)
            flight.Invalidate();
    }

    // A caller cancelled: the last one to leave a running flight abandons it, and it leaves the table in the
    // same lock, so no caller can join it after that
    private void Leave(string key, Flight flight)
    {
        lock (_lock)
        {
            if (--flight.Waiters > 0 || flight.Ended)
                return;

            Detach(key, flight);
        }

        flight.Abandon();
    }

    // The flight leaves the table before it completes: a caller that arrives after the value was stored
    // reads it from the cache instead of joining a finished flight
    private async Task LeadAsync(string key, Flight flight, Func<Flight, Task<T>> compute)
    {
        T value;
        try
        {
            value = await compute(flight);
        }
        catch (Exception ex)
        {
            End(key, flight);
            flight.Fail(ex);
            return;
        }

        End(key, flight);
        flight.Complete(value);
    }

    private void End(string key, Flight flight)
    {
        lock (_lock)
        {
            flight.Ended = true;
            Detach(key, flight);
        }
    }

    // Under the lock: removes the flight when it is still the key's (an invalidation may have replaced it)
    private void Detach(string key, Flight flight)
    {
        if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
            _inFlight.Remove(key);
    }
}
