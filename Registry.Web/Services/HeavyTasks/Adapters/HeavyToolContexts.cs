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

/// <summary>Concrete execution context handed to a tool during <c>ExecuteAsync</c>.</summary>
public sealed class HeavyToolExecutionContext : IHeavyToolExecutionContext
{
    public HeavyToolExecutionContext(
        IDDB ddb, DatasetKey dataset, ClaimsPrincipal? caller, ILogger logger,
        string taskId, string? workDir, PerformContext hangfire, IBuildScheduler buildQueue, IDatasetBuilds builds)
    {
        Ddb = ddb;
        Dataset = dataset;
        Caller = caller;
        Logger = logger;
        TaskId = taskId;
        WorkDir = workDir;
        Hangfire = hangfire;
        BuildQueue = buildQueue;
        Builds = builds;
    }

    public DatasetKey Dataset { get; }
    public IBuildScheduler BuildQueue { get; }
    public IDatasetBuilds Builds { get; }

    public IDDB Ddb { get; }
    public ClaimsPrincipal? Caller { get; }
    public ILogger Logger { get; }
    public string TaskId { get; }
    public string? WorkDir { get; }
    public PerformContext Hangfire { get; }
}
