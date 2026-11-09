using System.Security.Claims;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Builds;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>Concrete validation context handed to a tool during validation/planning.</summary>
public sealed class HeavyToolValidationContext : IHeavyToolValidationContext
{
    /// <param name="publishedOutput">The output an earlier attempt of the task wrote, for the retry of a task
    /// (<see cref="PublishedOutputMarker"/>); null for a submit.</param>
    /// <param name="ownCreatedDataset">The dataset an earlier run of the task created (its write record), for the retry
    /// of a task; null for a submit.</param>
    public HeavyToolValidationContext(IDDB ddb, ClaimsPrincipal? caller, ILogger logger,
        string? publishedOutput = null, TaskCreatedDataset? ownCreatedDataset = null)
    {
        Ddb = ddb;
        Caller = caller;
        Logger = logger;
        PublishedOutput = publishedOutput;
        OwnCreatedDataset = ownCreatedDataset;
    }

    public IDDB Ddb { get; }
    public ClaimsPrincipal? Caller { get; }
    public ILogger Logger { get; }

    /// <summary>Null for a submit, which has no earlier attempt; the retry of a task passes what it wrote.</summary>
    public string? PublishedOutput { get; }

    /// <summary>Null for a submit; the retry of a task passes the dataset its write record names.</summary>
    public TaskCreatedDataset? OwnCreatedDataset { get; }

    /// <summary>Always false: a submit follows the overwrite rule.</summary>
    public bool QueuedBeforeOverwriteRule => false;
}

/// <summary>The task being executed: its id and its work folder (null when the tool produces no artifact).</summary>
public sealed record TaskRun(string TaskId, string? WorkDir);

/// <summary>
/// Concrete execution context handed to a tool during <c>ExecuteAsync</c>. A job host has no HTTP caller:
/// <see cref="Caller"/> is always null; <see cref="RequestedBy"/> names the user the task runs for.
/// </summary>
public sealed class HeavyToolExecutionContext : IHeavyToolExecutionContext
{
    private readonly TaskRun _run;

    public HeavyToolExecutionContext(
        IDDB ddb, DatasetKey dataset, ILogger logger, TaskRun run, PerformContext hangfire,
        BuildScheduler buildQueue, DdbJobs builds, bool queuedBeforeOverwriteRule = false,
        ITaskWriteRecorder? writeRecord = null, string? requestedBy = null)
    {
        RequestedBy = requestedBy;
        QueuedBeforeOverwriteRule = queuedBeforeOverwriteRule;
        WriteRecord = writeRecord ?? TaskWriteRecorder.Disabled(run.TaskId, dataset, "unknown");
        Ddb = ddb;
        Dataset = dataset;
        Logger = logger;
        _run = run;
        Hangfire = hangfire;
        BuildQueue = buildQueue;
        Builds = builds;
    }

    public DatasetKey Dataset { get; }
    public BuildScheduler BuildQueue { get; }
    public DdbJobs Builds { get; }

    public IDDB Ddb { get; }
    public ClaimsPrincipal? Caller => null;

    /// <inheritdoc />
    public string? RequestedBy { get; }

    public ILogger Logger { get; }
    public string TaskId => _run.TaskId;
    public string? WorkDir => _run.WorkDir;
    public PerformContext Hangfire { get; }

    /// <inheritdoc />
    public string? PublishedOutput => PublishedOutputMarker.Get(Hangfire);

    /// <inheritdoc />
    public bool QueuedBeforeOverwriteRule { get; }

    /// <inheritdoc />
    public void MarkOutputPublished(string path) => PublishedOutputMarker.Set(Hangfire, path);

    /// <inheritdoc />
    public ITaskWriteRecorder WriteRecord { get; }
}
