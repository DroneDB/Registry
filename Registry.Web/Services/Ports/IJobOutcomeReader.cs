#nullable enable

namespace Registry.Web.Services.Ports;

/// <summary>
/// The state of a background job and, once it is over, its outcome: the value it returned, or the message of the
/// exception it failed with.
/// </summary>
public sealed record JobOutcome<TResult>(string JobId, string State, TResult? Result, string? Error)
    where TResult : class;

/// <summary>Reads the outcome of the background jobs of one kind.</summary>
public interface IJobOutcomeReader
{
    /// <summary>
    /// The outcome of the job <paramref name="jobId"/> if it is a job of <typeparamref name="TJob"/>; null when there
    /// is no such job, or it runs another class (a caller only sees the jobs it knows).
    /// </summary>
    JobOutcome<TResult>? Find<TJob, TResult>(string jobId) where TResult : class;
}
