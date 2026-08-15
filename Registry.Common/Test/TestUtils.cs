using System;
using System.Threading.Tasks;

namespace Registry.Common.Test;

/// <summary>
/// Async test helpers: time-bounded awaits that keep hanging operations from stalling the suite.
/// </summary>
public static class TestUtils
{
    /// <summary>
    /// Awaits <paramref name="task"/> for at most <paramref name="timeout"/> and reports whether it
    /// finished in time (completing or throwing both count as finished). The original task is always
    /// returned so callers can then await it for the result or the thrown exception.
    /// </summary>
    public static async Task<Task<T>> AwaitWithin<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (!ReferenceEquals(completed, task))
            throw new TimeoutException($"Task did not complete within {timeout}");

        return task;
    }

    /// <summary>
    /// Non-generic overload for operations exposed as a bare <see cref="Task"/>.
    /// </summary>
    public static async Task<Task> AwaitWithin(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (!ReferenceEquals(completed, task))
            throw new TimeoutException($"Task did not complete within {timeout}");

        return task;
    }
}
