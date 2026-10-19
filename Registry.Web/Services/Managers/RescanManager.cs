#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>Default <see cref="IRescanManager"/>.</summary>
public class RescanManager : IRescanManager
{
    private readonly IAuthManager _authManager;
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly RescanRebuild _rebuild;
    private readonly ILogger<RescanManager> _logger;

    public RescanManager(
        IAuthManager authManager,
        RegistryContext context,
        IDdbManager ddbManager,
        IBackgroundJobsProcessor backgroundJob,
        RescanRebuild rebuild,
        ILogger<RescanManager> logger)
    {
        _authManager = authManager;
        _context = context;
        _ddbManager = ddbManager;
        _backgroundJob = backgroundJob;
        _rebuild = rebuild;
        _logger = logger;
    }

    public async Task<RescanResultDto> RescanDatasetIndex(string orgSlug, string dsSlug, RescanOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(orgSlug))
            throw new ArgumentException("Organization slug is required", nameof(orgSlug));

        if (string.IsNullOrWhiteSpace(dsSlug))
            throw new ArgumentException("Dataset slug is required", nameof(dsSlug));

        options ??= new RescanOptions();
        var filter = RescanFilter.Parse(options.Types, options.Extensions);

        var dataset = await _context.Datasets
            .Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.Organization.Slug == orgSlug && d.Slug == dsSlug);

        if (dataset == null)
            throw new ArgumentException($"Dataset '{orgSlug}/{dsSlug}' not found");

        // Allow admins or users with write access to the dataset
        if (!await _authManager.IsUserAdmin() &&
            !await _authManager.RequestAccess(dataset, AccessType.Write))
            throw new UnauthorizedException("Only admins or users with write access can rescan dataset index");

        _logger.LogInformation("Rescanning index for dataset {OrgSlug}/{DsSlug} ({Filter})",
            orgSlug, dsSlug, filter);

        var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);
        var results = ddb.RescanIndex(filter.ToOptions(options.StopOnError));

        var user = await _authManager.GetCurrentUser();
        var meta = new IndexPayload(orgSlug, dsSlug, null, user?.Id ?? MagicStrings.AutoBuildServiceUserId);
        await _rebuild.RunAsync(orgSlug, dsSlug, ddb, filter, results, new QueuedBuilds(_backgroundJob, ddb, meta));

        var entries = results.Select(r => new RescanEntryResultDto
        {
            Path = r.Path,
            Hash = r.Hash,
            Success = r.Success,
            Error = r.Error
        }).ToArray();

        _logger.LogInformation("Rescan completed for {OrgSlug}/{DsSlug}: {Total} entries processed, {Success} successful, {Errors} errors",
            orgSlug, dsSlug, entries.Length, entries.Count(e => e.Success), entries.Count(e => !e.Success));

        return new RescanResultDto
        {
            OrganizationSlug = orgSlug,
            DatasetSlug = dsSlug,
            TotalProcessed = entries.Length,
            SuccessCount = entries.Count(e => e.Success),
            ErrorCount = entries.Count(e => !e.Success),
            Entries = entries
        };
    }

    public async Task<RescanJobDto> Rescan(RescanRequestDto request)
    {
        if (!await _authManager.IsUserAdmin())
            throw new UnauthorizedException("Only admins can perform system related tasks");

        ArgumentNullException.ThrowIfNull(request);
        var scope = DatasetScope.Parse(request.OrganizationSlug, request.DatasetSlug);
        var filter = RescanFilter.Parse(request.Types, request.Extensions).RequireNotEmpty();
        scope.EnsureFound(await scope.Apply(_context.Datasets).CountAsync());

        var user = await _authManager.GetCurrentUser();
        var orgSlug = scope.OrganizationSlug;
        var dsSlug = scope.DatasetSlug;
        var types = filter.TypesArgument;
        var extensions = filter.ExtensionsArgument;
        var stopOnError = request.StopOnError;
        var userId = user?.Id;

        var jobId = _backgroundJob.Enqueue<IndexRescanService>(s =>
            s.SubmitAsync(orgSlug, dsSlug, types, extensions, stopOnError, userId, null));

        _logger.LogInformation("Enqueued system rescan ({Filter}) of {Scope} as job {JobId}", filter, scope, jobId);

        return new RescanJobDto { JobId = jobId };
    }

    // Queued build jobs: a request must not run builds itself.
    private sealed class QueuedBuilds(IBackgroundJobsProcessor jobs, IDDB ddb, IndexPayload meta) : IRescanBuildRunner
    {
        public void BuildDataset() =>
            jobs.EnqueueIndexed(() => HangfireUtils.BuildWrapper(ddb, null, true, null), meta);

        public void BuildEntry(RescanResult entry)
        {
            var path = entry.Path;
            jobs.EnqueueIndexed(() => HangfireUtils.BuildWrapper(ddb, path, true, null),
                meta with { Hash = entry.Hash, Path = path });
        }

        public void BuildPending() =>
            jobs.EnqueueIndexed(() => HangfireUtils.BuildPendingWrapper(ddb, null), meta);
    }
}
