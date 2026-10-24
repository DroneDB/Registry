#nullable enable
using System;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>Reads the outcome of the background jobs of one kind.</summary>
public sealed class HangfireJobOutcomeReader
{
    // The keys of Hangfire's state data
    private const string ResultKey = "Result";
    private const string ExceptionMessageKey = "ExceptionMessage";

    private readonly JobStorage _storage;

    public HangfireJobOutcomeReader(JobStorage storage) => _storage = storage;

    /// <summary>
    /// The outcome of the job <paramref name="jobId"/> if it is a job of <typeparamref name="TJob"/>; null when there
    /// is no such job, or it runs another class (a caller only sees the jobs it knows).
    /// </summary>
    public JobOutcome<TResult>? Find<TJob, TResult>(string jobId) where TResult : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var connection = _storage.GetConnection();
        var job = connection.GetJobData(jobId);
        if (job?.Job?.Type != typeof(TJob))
            return null;

        var state = connection.GetStateData(jobId);
        var name = state?.Name ?? job.State ?? nameof(JobStatus.Unknown);
        string? data = null;
        if (state != null)
            state.Data.TryGetValue(name == SucceededState.StateName ? ResultKey : ExceptionMessageKey, out data);

        if (name == SucceededState.StateName)
            return new JobOutcome<TResult>(jobId, name,
                data == null ? null : SerializationHelper.Deserialize<TResult>(data, SerializationOption.User), null);

        return new JobOutcome<TResult>(jobId, name, null, name == FailedState.StateName ? data : null);
    }
}
