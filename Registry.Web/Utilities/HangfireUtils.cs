#nullable enable
using System;
using Hangfire;
using Hangfire.Server;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Attributes;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Utilities;

/// <summary>
/// The jobs as Hangfire stored them in earlier releases: exactly the type, method names and
/// parameter types that production storage holds (enqueued, scheduled, retrying jobs), so each
/// still deserializes and runs after the upgrade. Every method only forwards, in the same job
/// (same job id, JobIndex row and filters), to the job that replaced it. Activated by DI like any
/// job class (Hangfire stores whether a method is static nowhere). Remove after one release.
/// </summary>
/// <remarks>
/// A stored <see cref="IDDB"/> argument is the <c>Registry.Adapters.DroneDB.DDB</c> JSON
/// (<c>DatasetFolderPath</c>): read as a folder name only, mapped back to organization and
/// <c>InternalRef</c> by <see cref="IDdbManager.TryParseDatasetFolder"/>. A job's retry policy is
/// read from its own class and method, so each shim keeps the policy it was stored with (the
/// class's, <see cref="DdbRetriedJob"/>, unless the method says otherwise).
/// </remarks>
public sealed class HangfireUtils : DdbRetriedJob
{
    private readonly DdbJobs _ddbJobs;
    private readonly IDdbManager _ddbManager;
    private readonly FileJobs _files;
    private readonly ExpiredRecurringJobsSweep _expiredJobs;
    private readonly HealthCheckJob _healthCheck;

    public HangfireUtils(DdbJobs ddbJobs, IDdbManager ddbManager, FileJobs files,
        ExpiredRecurringJobsSweep expiredJobs, HealthCheckJob healthCheck)
    {
        _ddbJobs = ddbJobs ?? throw new ArgumentNullException(nameof(ddbJobs));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _expiredJobs = expiredJobs ?? throw new ArgumentNullException(nameof(expiredJobs));
        _healthCheck = healthCheck ?? throw new ArgumentNullException(nameof(healthCheck));
    }

    /// <summary>Stored <see cref="DdbJobs.Build"/>, with its policy (no retry of a retryable build failure).</summary>
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [15, 60, 180, 600, 1800],
        OnAttemptsExceeded = AttemptsExceededAction.Fail,
        OnlyOn = [typeof(DdbBusyException), typeof(DdbBuildInProgressException)])]
    public void BuildWrapper(IDDB ddb, string path, bool force, PerformContext context)
    {
        var (orgSlug, internalRef) = Locate(ddb);
        _ddbJobs.Build(orgSlug, internalRef, path, force, context);
    }

    /// <summary>Stored <see cref="DdbJobs.BuildPending"/>.</summary>
    public void BuildPendingWrapper(IDDB ddb, PerformContext context)
    {
        var (orgSlug, internalRef) = Locate(ddb);
        _ddbJobs.BuildPending(orgSlug, internalRef, context);
    }

    /// <summary>Stored <see cref="DdbJobs.Cleanup"/>.</summary>
    public void CleanupWrapper(IDDB ddb, PerformContext context)
    {
        var (orgSlug, internalRef) = Locate(ddb);
        _ddbJobs.Cleanup(orgSlug, internalRef, context);
    }

    /// <summary>Stored <see cref="DdbJobs.GenerateThumbnail"/>.</summary>
    public void GenerateThumbnailWrapper(IDDB ddb, string path, int size, string dest, PerformContext context)
    {
        var (orgSlug, internalRef) = Locate(ddb);
        _ddbJobs.GenerateThumbnail(orgSlug, internalRef, path, size, dest, context);
    }

    /// <summary>Stored <see cref="DdbJobs.MaskBorders"/>.</summary>
    public void MaskBordersWrapper(IDDB ddb, string inputPath, string outputPath, int nearDist, bool white,
        PerformContext context)
    {
        var (orgSlug, internalRef) = Locate(ddb);
        _ddbJobs.MaskBorders(orgSlug, internalRef, inputPath, outputPath, nearDist, white, context);
    }

    /// <summary>Stored <see cref="FileJobs.Delete"/> (artifact TTLs scheduled hours ahead).</summary>
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public void SafeDelete(string path, PerformContext context) => _files.Delete(path, context);

    /// <summary>Stored <see cref="ExpiredRecurringJobsSweep.Run"/> (the recurring definition is replaced at startup).</summary>
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public void CleanupExpiredJobs(PerformContext context) => _expiredJobs.Run(context, JobCancellation.Of(context));

    /// <summary>Stored <see cref="HealthCheckJob.Ping"/>.</summary>
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [JobExpiration(ExpirationTimeoutInMinutes = 5)]
    public void DummyJob(PerformContext context) => _healthCheck.Ping(context);

    private (string OrgSlug, Guid InternalRef) Locate(IDDB ddb)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        return _ddbManager.TryParseDatasetFolder(ddb.DatasetFolderPath, out var orgSlug, out var internalRef)
            ? (orgSlug, internalRef)
            : throw new InvalidOperationException(
                $"The job names the dataset folder '{ddb.DatasetFolderPath}', which is not a dataset folder under the datasets path");
    }
}
