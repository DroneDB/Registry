#nullable enable
using System;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// <see cref="IJobOutcomeReader"/> over Hangfire's storage: the job's current state, the value a succeeded job
/// returned (its <see cref="SucceededState"/> data) and the message a failed one threw (its <see cref="FailedState"/>
/// data).
/// </summary>
public sealed class HangfireJobOutcomeReader : IJobOutcomeReader
{
    // The keys of Hangfire's state data
    private const string ResultKey = "Result";
    private const string ExceptionMessageKey = "ExceptionMessage";

    private readonly JobStorage _storage;

    public HangfireJobOutcomeReader(JobStorage storage) => _storage = storage;

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
