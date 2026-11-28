using System;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Common;

/// <summary>
/// Work whose failure must not fail its caller: a cleanup, a notification, a record kept for display, one item of a
/// sweep that goes on with the others, or a read that has a safe answer when it cannot be made. The one place that
/// catches every failure of such work: the failure goes to the caller's handler, which logs it with the context only
/// the caller knows (and gives the answer of a read), and the caller goes on. A cancellation the caller asked for
/// (<see cref="OperationCanceledException"/> while <c>ct</c> is cancelled) is not a failure of the work: it is thrown
/// as the work threw it. Any other exception, a cancellation the caller did not ask for included, goes to the handler.
/// </summary>
public static class BestEffort
{
    /// <summary>Runs <paramref name="work"/>; a failure goes to <paramref name="onFailure"/>.</summary>
    public static void Run(Action work, Action<Exception> onFailure, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(onFailure);
        Get(() =>
        {
            work();
            return true;
        }, ex =>
        {
            onFailure(ex);
            return false;
        }, ct);
    }

    /// <summary>The value of <paramref name="read"/>, or the answer <paramref name="onFailure"/> gives to its failure.</summary>
    public static T Get<T>(Func<T> read, Func<Exception, T> onFailure, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(onFailure);
        try
        {
            return read();
        }
        catch (Exception ex) when (!CancelledByCaller(ex, ct))
        {
            return onFailure(ex);
        }
    }

    /// <summary>Runs <paramref name="work"/>; a failure goes to <paramref name="onFailure"/>.</summary>
    public static Task RunAsync(Func<Task> work, Action<Exception> onFailure, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(onFailure);
        return GetAsync(async () =>
        {
            await work();
            return true;
        }, ex =>
        {
            onFailure(ex);
            return false;
        }, ct);
    }

    /// <summary>The value of <paramref name="read"/>, or the answer <paramref name="onFailure"/> gives to its failure.</summary>
    public static async Task<T> GetAsync<T>(Func<Task<T>> read, Func<Exception, T> onFailure,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(onFailure);
        try
        {
            return await read();
        }
        catch (Exception ex) when (!CancelledByCaller(ex, ct))
        {
            return onFailure(ex);
        }
    }

    private static bool CancelledByCaller(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;
}
