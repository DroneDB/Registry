#nullable enable
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
    public HeavyToolValidationContext(IDDB ddb, ClaimsPrincipal? caller, ILogger logger)
    {
        Ddb = ddb;
        Caller = caller;
        Logger = logger;
    }

    public IDDB Ddb { get; }
    public ClaimsPrincipal? Caller { get; }
    public ILogger Logger { get; }
}

/// <summary>The task being executed: its id and its work folder (null when the tool produces no artifact).</summary>
public sealed record TaskRun(string TaskId, string? WorkDir);

/// <summary>
/// Concrete execution context handed to a tool during <c>ExecuteAsync</c>. A job host has no HTTP caller:
/// <see cref="Caller"/> is always null.
/// </summary>
public sealed class HeavyToolExecutionContext : IHeavyToolExecutionContext
{
    private readonly TaskRun _run;

    public HeavyToolExecutionContext(
        IDDB ddb, DatasetKey dataset, ILogger logger, TaskRun run, PerformContext hangfire,
        BuildScheduler buildQueue, DdbJobs builds)
    {
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
    public ILogger Logger { get; }
    public string TaskId => _run.TaskId;
    public string? WorkDir => _run.WorkDir;
    public PerformContext Hangfire { get; }
}
