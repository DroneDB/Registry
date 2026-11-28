using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test.Support;

/// <summary>Test shorthands over <see cref="DatasetIndexQueue"/>.</summary>
public static class DatasetIndexQueueTestExtensions
{
    /// <summary>Enqueues one path and returns its entry (the first failure is thrown).</summary>
    public static async Task<Entry> EnqueueAsync(this DatasetIndexQueue queue, DatasetKey dataset, string path,
        CancellationToken ct = default)
    {
        var results = await queue.EnqueueAsync(dataset, [path], ct);
        return results[0];
    }

    /// <summary>
    /// Returns once the lane of <paramref name="dataset"/> has run every batch enqueued before, with the outcomes it
    /// tells the callers that stopped waiting: a lane tells them before its next batch, and the probe that this call
    /// enqueues comes after.
    /// </summary>
    public static async Task DrainedAsync(this DatasetIndexQueue queue, DatasetKey dataset) =>
        await queue.EnqueueAsync(dataset, ["drained.probe"]);
}
