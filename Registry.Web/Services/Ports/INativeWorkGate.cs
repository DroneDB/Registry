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
    /// <paramref name="ct"/> cancels the wait for a slot only, never running work.
    /// </summary>
    Task<T> RunAsync<T>(NativeLane lane, Func<T> work, CancellationToken ct = default);

    /// <summary>Runs asynchronous work once the lane has a free slot.</summary>
    Task<T> RunAsync<T>(NativeLane lane, Func<Task<T>> work, CancellationToken ct = default);

    /// <summary>Calls running on <paramref name="lane"/> right now.</summary>
    int InFlight(NativeLane lane);
}
