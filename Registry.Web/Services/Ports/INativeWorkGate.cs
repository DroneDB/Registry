#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.Ports;

/// <summary>The kinds of native work that share a concurrency budget.</summary>
public enum NativeLane
{
    /// <summary>Map tiles and map renders: many small, latency-sensitive calls.</summary>
    Tiles,

    /// <summary>Thumbnails and coverage renders: fewer, larger calls.</summary>
    Heavy
}

/// <summary>
/// Bounds how many native renders run at once per lane (REG-07 §5.2), so a burst of tile or
/// thumbnail misses queues instead of running hundreds of GDAL/PDAL calls in parallel.
/// Never nest two calls on the same lane: the inner one could wait for a slot its own caller holds.
/// </summary>
public interface INativeWorkGate
{
    /// <summary>
    /// Runs <paramref name="work"/> on a thread-pool thread once the lane has a free slot.
    /// <paramref name="ct"/> (the request's or the job's) cancels the wait for a slot and the work: it does not
    /// start once canceled, and while it runs it is the <see cref="Registry.Ports.DroneDB.DdbCancellation"/> of
    /// its native calls, which libddb stops at their next checkpoint (XI-07, RQ-13).
    /// </summary>
    Task<T> RunAsync<T>(NativeLane lane, Func<T> work, CancellationToken ct = default);

    /// <summary>Runs asynchronous work once the lane has a free slot, canceled like the synchronous form.</summary>
    Task<T> RunAsync<T>(NativeLane lane, Func<Task<T>> work, CancellationToken ct = default);
}

/// <summary>Native work without a result through <see cref="INativeWorkGate"/>.</summary>
public static class NativeWorkGateExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> on a thread-pool thread once the lane has a free slot (see
    /// <see cref="INativeWorkGate.RunAsync{T}(NativeLane, Func{T}, CancellationToken)"/>).
    /// </summary>
    public static Task RunAsync(this INativeWorkGate gate, NativeLane lane, Action work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(work);
        return gate.RunAsync(lane, () =>
        {
            work();
            return true;
        }, ct);
    }
}
