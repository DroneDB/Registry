namespace Registry.Web.Models.DTO;

// Named answers of the actions that used to return an anonymous object (RQ-14): same member names, same types,
// so the JSON is unchanged, and the API description names them.

/// <summary>The answer of an action that removed something: whether it was removed.</summary>
public sealed record DeletedDto(bool Deleted);

/// <summary>The answer of clearing a dataset's task history: how many tasks were removed.</summary>
public sealed record TasksClearedDto(int Cleared);

/// <summary>The answer of retrying a task: whether it was queued again.</summary>
public sealed record TaskRequeuedDto(bool Requeued);

/// <summary>The answer of an action that enqueued a background job: the job's id.</summary>
public sealed record JobAcceptedDto(string JobId);

/// <summary>The answer of an action that only reports success.</summary>
public sealed record OkDto(bool Ok)
{
    /// <summary>The answer of an operation that completed.</summary>
    public static readonly OkDto Success = new(true);
}
