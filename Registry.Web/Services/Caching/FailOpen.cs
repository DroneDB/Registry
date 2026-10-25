#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Registry.Web.Services.Caching;

/// <summary>
/// A cache call that never fails the request: an error is logged as a warning and the fallback
/// returned, so a cache that is down costs a recomputation, not an error. A cancellation the caller asked for
/// still propagates.
/// </summary>
internal static class FailOpen
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> call, T fallback, ILogger logger, string what, string key,
        CancellationToken ct = default)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Cache: {What} failed for {Key}", what, key);
            return fallback;
        }
    }

    public static Task RunAsync(Func<Task> call, ILogger logger, string what, string key, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            await call();
            return true;
        }, false, logger, what, key, ct);
}
