#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.Caching;

/// <summary>
/// One computation per key at a time within the process: concurrent callers of a key share the computation
/// started by the first (single-flight), so a burst of cache misses computes a value once. The owner of the
/// cache detaches a flight when it invalidates the key: later callers start a new flight, and the detached one
/// is marked so its result, computed from the state before the invalidation, is not stored.
/// </summary>
/// <typeparam name="T">The value computed.</typeparam>
public sealed class SingleFlight<T>
{
    private readonly ConcurrentDictionary<string, Flight> _inFlight = new(StringComparer.Ordinal);

    /// <summary>One computation in progress.</summary>
    public sealed class Flight
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _invalidated;

        internal Task<T> Completion => _completion.Task;

        /// <summary>The key was invalidated while the value was computed: do not store the value.</summary>
        public bool Invalidated => _invalidated;

        internal void Invalidate() => _invalidated = true;
        internal void Complete(T value) => _completion.SetResult(value);
        internal void Fail(Exception error) => _completion.SetException(error);
    }

    /// <summary>
    /// Joins the computation of <paramref name="key"/>, or starts it with <paramref name="compute"/>. The
    /// computation runs to completion for every caller sharing it, so it does not take any caller's token;
    /// <paramref name="ct"/> only ends this caller's wait.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="compute">Computes the value; called by the first caller only, with its flight.</param>
    /// <param name="ct">Ends the wait of this caller.</param>
    /// <returns>The value, or the computation's exception, for every caller of the flight.</returns>
    public async Task<T> RunAsync(string key, Func<Flight, Task<T>> compute, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(compute);

        var flight = new Flight();
        var current = _inFlight.GetOrAdd(key, flight);
        if (ReferenceEquals(current, flight))
            await LeadAsync(key, flight, compute);

        return await current.Completion.WaitAsync(ct);
    }

    /// <summary>Detaches the flight of <paramref name="key"/>, if any, and marks it invalidated.</summary>
    public void Invalidate(string key)
    {
        if (_inFlight.TryRemove(key, out var flight))
            flight.Invalidate();
    }

    /// <summary>Detaches every flight whose key starts with <paramref name="prefix"/> and marks it invalidated.</summary>
    public void InvalidatePrefix(string prefix)
    {
        foreach (var entry in _inFlight)
        {
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal) && _inFlight.TryRemove(entry))
                entry.Value.Invalidate();
        }
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
            _inFlight.TryRemove(new KeyValuePair<string, Flight>(key, flight));
            flight.Fail(ex);
            return;
        }

        _inFlight.TryRemove(new KeyValuePair<string, Flight>(key, flight));
        flight.Complete(value);
    }
}
