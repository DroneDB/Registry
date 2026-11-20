using System;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Datasets;

/// <summary>
/// A read of one dataset in an answer about many (a map, a listing): one dataset that cannot be read must not take
/// the whole answer down.
/// </summary>
public static class PerDatasetRead
{
    /// <summary>
    /// The result of <paramref name="read"/>, or null when it fails for good (its folder is gone, its index is
    /// unreadable): <paramref name="onUnreadable"/> is told why, for the caller to log it in its own words. A
    /// cancellation and a failure that can pass (<see cref="DdbErrors.IsTransient"/>: busy, a build holds the index,
    /// disk full, shutting down) propagate: the first ends the request, the second answers 503 and the caller tries
    /// again, rather than keeping an answer with a hole in it.
    /// </summary>
    public static async Task<T?> OrNullAsync<T>(Func<Task<T?>> read, Action<Exception> onUnreadable) where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !DdbErrors.IsTransient(ex))
        {
            onUnreadable(ex);
            return null;
        }
    }
}
