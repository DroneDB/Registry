#nullable enable

namespace Registry.Web.Services.Ports;

/// <summary>
/// The state of a background job and, once it is over, its outcome: the value it returned, or the message of the
/// exception it failed with.
/// </summary>
public sealed record JobOutcome<TResult>(string JobId, string State, TResult? Result, string? Error)
    where TResult : class;
