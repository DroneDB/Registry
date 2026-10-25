#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Utilities;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="INativeWorkGate"/>: one semaphore per lane, widths from
/// the host budget (<see cref="HostResourceBudget"/>: <see cref="NativeConcurrencySettings"/> on the cores of this process). Reports the calls in flight per lane as the
/// <c>registry.native.in_flight</c> gauge (meter <c>Registry.NativeWork</c>). The caller's token cancels the
/// wait and runs the work as its <see cref="DdbCancellation"/>.
/// </summary>
public sealed class NativeWorkGate : INativeWorkGate, IDisposable
{
    public const string MeterName = "Registry.NativeWork";

    private static readonly Lazy<NativeWorkGate> SharedDefault = new(() => new NativeWorkGate(new NativeConcurrencySettings()));

    /// <summary>
    /// A process-wide gate with the default widths, for components constructed without DI (tests);
    /// the host registers its own configured singleton.
    /// </summary>
    public static INativeWorkGate Default => SharedDefault.Value;

    private readonly SemaphoreSlim _tiles;
    private readonly SemaphoreSlim _heavy;
    private readonly int _tilesWidth;
    private readonly int _heavyWidth;
    private readonly Meter _meter;

    /// <summary>The host's gate: the lane widths of the host budget.</summary>
    public NativeWorkGate(HostResourceBudget budget) : this(budget.RenderTiles, budget.RenderHeavy)
    {
    }

    /// <summary>A gate with <paramref name="settings"/> on every core of the process (tests).</summary>
    public NativeWorkGate(NativeConcurrencySettings settings) : this(settings.TilesOrDefault, settings.HeavyOrDefault)
    {
    }

    private NativeWorkGate(int tilesWidth, int heavyWidth)
    {
        _tilesWidth = tilesWidth;
        _heavyWidth = heavyWidth;
        _tiles = new SemaphoreSlim(_tilesWidth, _tilesWidth);
        _heavy = new SemaphoreSlim(_heavyWidth, _heavyWidth);

        _meter = new Meter(MeterName);
        _meter.CreateObservableGauge("registry.native.in_flight", () => new List<Measurement<int>>
        {
            new(InFlight(NativeLane.Tiles), new KeyValuePair<string, object?>("lane", "tiles")),
            new(InFlight(NativeLane.Heavy), new KeyValuePair<string, object?>("lane", "heavy"))
        }, description: "Native renders running per lane");
    }

    public Task<T> RunAsync<T>(NativeLane lane, Func<T> work, CancellationToken ct = default) =>
        RunAsync(lane, () => Task.Run(work), ct);

    public async Task<T> RunAsync<T>(NativeLane lane, Func<Task<T>> work, CancellationToken ct = default)
    {
        var semaphore = For(lane);
        await semaphore.WaitAsync(ct);
        try
        {
            // A slot freed after the caller went away: the work is not started for nobody
            ct.ThrowIfCancellationRequested();

            // The work's native calls get the token (libddb cancel tokens); Task.Run flows it in
            using (DdbCancellation.Enter(ct))
                return await work();
        }
        finally
        {
            semaphore.Release();
        }
    }

    public int InFlight(NativeLane lane) => lane == NativeLane.Tiles
        ? _tilesWidth - _tiles.CurrentCount
        : _heavyWidth - _heavy.CurrentCount;

    private SemaphoreSlim For(NativeLane lane) => lane == NativeLane.Tiles ? _tiles : _heavy;

    public void Dispose()
    {
        _meter.Dispose();
        _tiles.Dispose();
        _heavy.Dispose();
    }
}
