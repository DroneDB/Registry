#nullable enable
using Hangfire;
using Registry.Ports.DroneDB;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// Base of the job classes that work on datasets: their one retry policy. Transient DroneDB
/// contention (the index or the build lock is busy, libddb reports a retryable failure such as
/// low disk or a tool idle timeout, or the pending coordinator lock is held) gets a backoff
/// chain; anything else fails fast without burning worker slots. Hangfire reads a job's filters
/// from its class (inherited) and its method; a method attribute overrides this one
/// (<see cref="DdbJobs.Build"/>).
/// </summary>
[AutomaticRetry(Attempts = 5, DelaysInSeconds = [15, 60, 180, 600, 1800],
    OnAttemptsExceeded = AttemptsExceededAction.Fail,
    OnlyOn = [typeof(DdbBusyException), typeof(DdbBuildInProgressException), typeof(DdbRetryableException),
        typeof(PendingBuildCoordinatorContentionException)])]
public abstract class DdbRetriedJob;
