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

/// <summary>
/// The run of the task being executed: its id, its Hangfire job, its work folder (null when the tool produces no
/// artifact), its write record (none: a record that refuses every write), the user it runs for (null for a task of the
/// system) and whether its request was queued before the overwrite rule.
/// </summary>
public sealed record TaskRun(string TaskId, string? WorkDir, PerformContext Hangfire,
    ITaskWriteRecorder? WriteRecord = null, string? RequestedBy = null, bool QueuedBeforeOverwriteRule = false);

/// <summary>
/// Concrete execution context handed to a tool during <c>ExecuteAsync</c>. A job host has no HTTP caller:
/// <see cref="Caller"/> is always null; <see cref="RequestedBy"/> names the user the task runs for.
/// </summary>
public sealed class HeavyToolExecutionContext : IHeavyToolExecutionContext
{
    private readonly TaskRun _run;

    public HeavyToolExecutionContext(IDDB ddb, DatasetKey dataset, ILogger logger, TaskRun run,
        BuildScheduler buildQueue, DdbJobs builds, IStorageQuota? storage = null)
    {
        Storage = storage ?? UnlimitedStorageQuota.Instance;
        WriteRecord = run.WriteRecord ?? TaskWriteRecorder.Disabled(run.TaskId, dataset, "unknown");
        Ddb = ddb;
        Dataset = dataset;
        Logger = logger;
        _run = run;
        BuildQueue = buildQueue;
        Builds = builds;
    }

    public DatasetKey Dataset { get; }
    public BuildScheduler BuildQueue { get; }
    public DdbJobs Builds { get; }

    public IDDB Ddb { get; }
    public ClaimsPrincipal? Caller => null;

    /// <inheritdoc />
    public string? RequestedBy => _run.RequestedBy;

    /// <inheritdoc />
    public IStorageQuota Storage { get; }

    public ILogger Logger { get; }
    public string TaskId => _run.TaskId;
    public string? WorkDir => _run.WorkDir;
    public PerformContext Hangfire => _run.Hangfire;

    /// <inheritdoc />
    public string? PublishedOutput => PublishedOutputMarker.Get(Hangfire);

    /// <inheritdoc />
    public bool QueuedBeforeOverwriteRule => _run.QueuedBeforeOverwriteRule;

    /// <inheritdoc />
    public void MarkOutputPublished(string path) => PublishedOutputMarker.Set(Hangfire, path);

    /// <inheritdoc />
    public ITaskWriteRecorder WriteRecord { get; }
}
