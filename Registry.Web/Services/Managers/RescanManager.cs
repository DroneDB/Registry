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
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>Default <see cref="IRescanManager"/>.</summary>
public class RescanManager : IRescanManager
{
    private readonly IAuthManager _authManager;
    private readonly IDatasetAccessResolver _access;
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly IBuildScheduler _builds;
    private readonly RescanRebuild _rebuild;
    private readonly ILogger<RescanManager> _logger;

    public RescanManager(
        IAuthManager authManager,
        IDatasetAccessResolver access,
        RegistryContext context,
        IDdbManager ddbManager,
        IBackgroundJobsProcessor backgroundJob,
        IBuildScheduler builds,
        RescanRebuild rebuild,
        ILogger<RescanManager> logger)
    {
        _authManager = authManager;
        _access = access;
        _context = context;
        _ddbManager = ddbManager;
        _backgroundJob = backgroundJob;
        _builds = builds;
        _rebuild = rebuild;
        _logger = logger;
    }

    public async Task<RescanResultDto> RescanDatasetIndex(string orgSlug, string dsSlug, RescanOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(orgSlug))
            throw new BadRequestException("Organization slug is required");

        if (string.IsNullOrWhiteSpace(dsSlug))
            throw new BadRequestException("Dataset slug is required");

        options ??= new RescanOptions();
        var filter = RescanFilter.Parse(options.Types, options.Extensions);

        var dataset = await _context.Datasets
            .Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.Organization.Slug == orgSlug && d.Slug == dsSlug);

        if (dataset == null)
            throw new BadRequestException($"Dataset '{orgSlug}/{dsSlug}' not found");

        // Write access (administrators have it on every dataset)
        await _access.EnsureAccessAsync(dataset, AccessType.Write);

        _logger.LogInformation("Rescanning index for dataset {OrgSlug}/{DsSlug} ({Filter})",
            orgSlug, dsSlug, filter);

        var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);
        var results = ddb.RescanIndex(filter.ToOptions(options.StopOnError));

        var user = await _authManager.GetCurrentUser();
        var origin = new BuildOrigin(user?.Id ?? MagicStrings.AutoBuildServiceUserId);
        var target = new BuildTarget(ddb, new DatasetKey(orgSlug, dataset.InternalRef), orgSlug, dsSlug);
        await _rebuild.RunAsync(orgSlug, dsSlug, ddb, filter, results, new QueuedBuilds(_builds, target, origin));

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
    private sealed class QueuedBuilds(IBuildScheduler builds, BuildTarget target, BuildOrigin origin)
        : IRescanBuildRunner
    {
        public Task BuildDataset() => builds.QueueDatasetAsync(target, force: true, origin);

        public Task BuildEntry(RescanResult entry) => builds.QueueAsync(target, entry.Path, entry.Hash, force: true, origin);

        public Task BuildPending() => builds.QueuePendingAsync(target, origin);
    }
}
