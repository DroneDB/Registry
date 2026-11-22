using Registry.Web.Utilities;

namespace Registry.Web.Exceptions;

/// <summary>
/// A request on a dataset that a task is importing or rescanning (HTTP 409): until the task ends the dataset can only be
/// deleted. <see cref="ApiErrorResponder"/> writes <see cref="Kind"/> and <see cref="TaskId"/> as the
/// <c>datasetBusy</c> and <c>taskId</c> members of the ProblemDetails body, so a client can follow the task.
/// </summary>
public sealed class DatasetBusyException : ConflictException
{
    public DatasetBusyException(string message, string kind, string taskId) : base(message)
    {
        Kind = kind;
        TaskId = taskId;
        // The same request is refused again until the task ends: the client waits for the task, not for a retry
        NoRetry = true;
    }

    /// <summary>What the task does to the dataset: <c>importing</c> or <c>rescanning</c>.</summary>
    public string Kind { get; }

    /// <summary>The task that holds the dataset.</summary>
    public string TaskId { get; }
}
