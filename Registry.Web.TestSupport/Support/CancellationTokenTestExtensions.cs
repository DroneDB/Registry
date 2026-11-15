using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Test.Support;

/// <summary>Test shorthands over <see cref="CancellationToken"/>.</summary>
public static class CancellationTokenTestExtensions
{
    /// <summary>
    /// A task that ends, cancelled, when <paramref name="ct"/> is cancelled, and never otherwise: a fake that stalls
    /// (a remote that never answers) awaits it to hold its caller until the caller gives up. No timer runs, so the
    /// test waits for the cancellation itself, not for a delay.
    /// </summary>
    public static Task WhenCancelled(this CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => cancelled.TrySetCanceled(ct));
        return cancelled.Task;
    }
}
