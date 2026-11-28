using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Common;

namespace Registry.Web.Services.Caching;

/// <summary>
/// A cache call that never fails the request: an error is logged as a warning and the fallback
/// returned, so a cache that is down costs a recomputation, not an error. A cancellation the caller asked for
/// still propagates.
/// </summary>
internal static class FailOpen
{
    public static Task<T> RunAsync<T>(Func<Task<T>> call, T fallback, ILogger logger, string what, string key,
        CancellationToken ct = default) =>
        BestEffort.GetAsync(call, ex =>
        {
            logger.LogWarning(ex, "Cache: {What} failed for {Key}", what, key);
            return fallback;
        }, ct);

    public static Task RunAsync(Func<Task> call, ILogger logger, string what, string key, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            await call();
            return true;
        }, false, logger, what, key, ct);
}
