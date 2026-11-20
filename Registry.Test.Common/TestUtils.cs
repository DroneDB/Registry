namespace Registry.Test.Common;

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
        await AwaitWithin((Task)task, timeout);
        return task;
    }

    /// <summary>
    /// Non-generic overload for operations exposed as a bare <see cref="Task"/>.
    /// </summary>
    public static async Task<Task> AwaitWithin(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            throw new TimeoutException($"Task did not complete within {timeout}");
        }
        catch when (task.IsCompleted)
        {
            // Finished by throwing: the caller awaits the returned task to observe the exception.
        }

        return task;
    }
}
