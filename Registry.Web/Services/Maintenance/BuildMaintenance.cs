#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Maintenance;

/// <summary>
/// Build and index maintenance across datasets (admin): the build cleanup, the system rescan, the job index
/// retention.
/// </summary>
public interface IBuildMaintenance
{
    /// <summary>
    /// Cleans up build artifacts and stale entries on one dataset (synchronously), all datasets in an
    /// organization or every dataset (one background job per dataset).
    /// </summary>
    Task<CleanupBuildResultDto> CleanupBuildAsync(CleanupBuildRequestDto? request, CancellationToken ct = default);

    /// <summary>
    /// Rescans the entries that match a type and/or extension filter in every dataset of a scope (all
    /// organizations, one organization or one dataset). Runs in the background: one rescan-index task per dataset
    /// with matching entries.
    /// </summary>
    /// <returns>The id of the background job that submits the dataset tasks.</returns>
    Task<RescanJobDto> RescanAsync(RescanRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// Removes terminal (Succeeded/Failed/Deleted) job index records older than <paramref name="retentionDays"/>
    /// (the configured retention when null).
    /// </summary>
    Task<CleanupJobIndicesResultDto> CleanupJobIndicesAsync(int? retentionDays = null, CancellationToken ct = default);
}

/// <summary>Default <see cref="IBuildMaintenance"/> (RQ-12, out of <c>SystemManager</c> and <c>RescanManager</c>).</summary>
public sealed class BuildMaintenance : IBuildMaintenance
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly IBuildRequester _requester;
    private readonly IJobIndexWriter _jobIndexWriter;
    private readonly JobScheduleOptions _jobs;
    private readonly ILogger<BuildMaintenance> _logger;

    public BuildMaintenance(RegistryContext context, IDdbManager ddbManager, IBackgroundJobsProcessor backgroundJob,
        IBuildRequester requester, IJobIndexWriter jobIndexWriter, IOptions<JobScheduleOptions> jobs,
        ILogger<BuildMaintenance> logger)
    {
        _context = context;
        _ddbManager = ddbManager;
        _backgroundJob = backgroundJob;
        _requester = requester;
        _jobIndexWriter = jobIndexWriter;
        _jobs = jobs.Value;
        _logger = logger;
    }

    public async Task<CleanupBuildResultDto> CleanupBuildAsync(CleanupBuildRequestDto? request, CancellationToken ct = default)
    {
        request ??= new CleanupBuildRequestDto();
        var scope = DatasetScope.Parse(request.OrganizationSlug, request.DatasetSlug);

        var targets = await scope.Apply(_context.Datasets.AsNoTracking().Include(d => d.Organization))
            .ToArrayAsync(ct);
        scope.EnsureFound(targets.Length);

        // Async (background jobs) when the request scope targets more than one dataset
        // (i.e. all orgs, or all datasets in one org). A single targeted dataset always
        // runs synchronously. This matches the documented API contract regardless of
        // how many datasets actually exist for the resolved scope.
        var async = !scope.IsSingleDataset;

        // Empty target set: nothing to enqueue. Return a synchronous, empty result so the
        // controller does not respond with 202 Accepted carrying a null JobId.
        if (targets.Length == 0)
        {
            _logger.LogInformation("CleanupBuild: no target datasets resolved; returning empty result");
            return new CleanupBuildResultDto { Async = false };
        }

        var result = new CleanupBuildResultDto { Async = async };

        if (async)
        {
            var userId = await _requester.UserIdAsync() ?? MagicStrings.AutoBuildServiceUserId;

            string? lastJobId = null;
            foreach (var ds in targets)
            {
                var meta = new IndexPayload(ds.Organization.Slug, ds.Slug, null, userId);
                lastJobId = _backgroundJob.EnqueueCleanup(new DatasetKey(ds.Organization.Slug, ds.InternalRef), meta);
                result.JobIds.Add(lastJobId);
            }

            result.JobId = lastJobId;
            _logger.LogInformation("Enqueued cleanup for {Count} datasets ({JobCount} jobs, last {JobId})",
                targets.Length, result.JobIds.Count, lastJobId);
            return result;
        }

        // Synchronous: single dataset only
        foreach (var ds in targets)
        {
            var key = $"{ds.Organization.Slug}/{ds.Slug}";
            try
            {
                var ddb = _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);
                var cleanup = ddb.Cleanup();

                result.Datasets[key] = new DatasetCleanupBuildDto
                {
                    RemovedEntries = cleanup.Entries ?? [],
                    RemovedBuilds = cleanup.Builds ?? []
                };

                _logger.LogInformation(
                    "Cleanup of {Key}: removed {EntryCount} entries, {BuildCount} build artifacts",
                    key, cleanup.Entries?.Length ?? 0, cleanup.Builds?.Length ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup failed for {Key}", key);
                result.Errors[key] = ex.Message;
            }
        }

        return result;
    }

    public async Task<CleanupJobIndicesResultDto> CleanupJobIndicesAsync(int? retentionDays = null, CancellationToken ct = default)
    {
        var days = retentionDays ?? _jobs.JobIndexRetentionDays;
        if (days <= 0)
            days = 60;

        var cutoff = DateTime.UtcNow.AddDays(-days);

        _logger.LogInformation("Manual JobIndex cleanup: removing terminal records older than {Days} days (before {Cutoff:u})",
            days, cutoff);

        // One statement: canceled, it deletes all or nothing
        var deleted = await _jobIndexWriter.DeleteTerminalBeforeAsync(cutoff, ct);

        _logger.LogInformation("Manual JobIndex cleanup completed: {Deleted} records removed", deleted);

        return new CleanupJobIndicesResultDto
        {
            DeletedCount = deleted,
            RetentionDays = days
        };
    }

    public async Task<RescanJobDto> RescanAsync(RescanRequestDto request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = DatasetScope.Parse(request.OrganizationSlug, request.DatasetSlug);
        var filter = RescanFilter.Parse(request.Types, request.Extensions).RequireNotEmpty();
        scope.EnsureFound(await scope.Apply(_context.Datasets).CountAsync(ct));

        var orgSlug = scope.OrganizationSlug;
        var dsSlug = scope.DatasetSlug;
        var types = filter.TypesArgument;
        var extensions = filter.ExtensionsArgument;
        var stopOnError = request.StopOnError;
        var userId = await _requester.UserIdAsync();

        var jobId = _backgroundJob.Enqueue<IndexRescanService>(s =>
            s.SubmitAsync(orgSlug, dsSlug, types, extensions, stopOnError, userId, null));

        _logger.LogInformation("Enqueued system rescan ({Filter}) of {Scope} as job {JobId}", filter, scope, jobId);

        return new RescanJobDto { JobId = jobId };
    }
}
