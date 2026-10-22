#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Objects;

/// <summary>Default <see cref="IBuildQueryService"/>.</summary>
public sealed class BuildQueryService : IBuildQueryService
{
    private readonly IDatasetAccessResolver _access;
    private readonly IBuildArtifactResolver _artifacts;
    private readonly DatasetPathPolicy _paths;
    private readonly EntryJobs _jobs;
    private readonly IJobIndexQuery _jobIndex;
    private readonly RegistryContext _context;
    private readonly ILogger<BuildQueryService> _logger;

    public BuildQueryService(IDatasetAccessResolver access, IBuildArtifactResolver artifacts, DatasetPathPolicy paths,
        EntryJobs jobs, IJobIndexQuery jobIndex, RegistryContext context, ILogger<BuildQueryService> logger)
    {
        _access = access;
        _artifacts = artifacts;
        _paths = paths;
        _jobs = jobs;
        _jobIndex = jobIndex;
        _context = context;
        _logger = logger;
    }

    public async Task BuildAsync(string orgSlug, string dsSlug, string? path, bool force = false,
        CancellationToken ct = default)
    {
        // You need write access to build
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        // No path names no entry (the dataset root has none)
        path ??= string.Empty;
        var entry = access.Ddb.GetEntry(path) ?? throw new NotFoundException($"Cannot find source entry: '{path}'");

        var build = _artifacts.GetBuild(access.Ddb, entry.Path);
        if (!build.CanBeBuilt())
        {
            _logger.LogInformation("'{EntryPath}' is not buildable, nothing to do here", entry.Path);
            return;
        }

        // A live build holds the lock (force skips the check: a stale lock of a crashed build is not a live
        // build, and force recovers from it)
        if (!force && build.HasLiveBuild())
            throw new ConflictException($"A build is already in progress for '{entry.Path}'");

        var jobId = await _jobs.EnqueueBuildAsync(access, orgSlug, dsSlug, path, entry.Hash, force, markPending: false);
        _logger.LogInformation("Building '{Path}' asynchronously, background job id is {JobId}", path, jobId);
    }

    public async Task<string> GetBuildFileAsync(string orgSlug, string dsSlug, string hash, string? path,
        CancellationToken ct = default)
    {
        var (ddb, file) = await ResolveBuildFilePathAsync(orgSlug, dsSlug, path, ct);

        // libddb resolves the file of the build and checks that it stays inside it (DDBResolveBuildFile)
        return _artifacts.ResolveFile(ddb, hash, file)
               ?? throw new NotFoundException($"'{file}' is not a file of the build '{hash}'");
    }

    public async Task<bool> CheckBuildFileAsync(string orgSlug, string dsSlug, string hash, string? path,
        CancellationToken ct = default)
    {
        var (ddb, file) = await ResolveBuildFilePathAsync(orgSlug, dsSlug, path, ct);
        try
        {
            return _artifacts.ResolveFile(ddb, hash, file) != null;
        }
        catch (DdbInvalidArgumentsException)
        {
            // A malformed hash names no build: the file does not exist (HEAD answers 404, as it always did)
            return false;
        }
    }

    public async Task<BuildDownload> GetBuildArtifactAsync(string orgSlug, string dsSlug, string entryPath,
        CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        var entry = ddb.GetEntry(entryPath)
                    ?? throw new NotFoundException($"Entry '{entryPath}' does not exist in the dataset.");

        // The artifact libddb offers as a download (manifest: downloadName), served with Registry's media type
        var build = _artifacts.GetBuild(ddb, entry.Path);
        if (_artifacts.GetDownload(build) is { } download)
            return download;

        // A build that is complete without a downloadable artifact, or no build of its own: nothing to download
        if (!build.IsOwnBuild() || build.Build!.State == BuildState.Complete)
            throw new BadRequestException($"Entry type '{entry.Type}' does not have a downloadable build artifact.");

        throw new NotFoundException(
            $"Build artifact not found for '{entryPath}'. The entry may not have been built yet.");
    }

    public async Task<IEnumerable<PendingBuildInfoDto>> GetPendingBuildsAsync(string orgSlug, string dsSlug,
        CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        return ddb.GetPendingBuildInfo().Select(p => new PendingBuildInfoDto
        {
            Path = p.Path,
            Hash = p.Hash,
            MissingDependencies = p.MissingDependencies,
            LastAttempt = DateTimeOffset.FromUnixTimeSeconds(p.LastAttempt).UtcDateTime
        });
    }

    public async Task<int> ClearCompletedBuildsAsync(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        var completed = (await _jobIndex.GetByOrgDsAsync(orgSlug, dsSlug, 0, int.MaxValue, ct))
            .Where(b => b.CurrentState is "Succeeded" or "Failed").ToList();

        _logger.LogInformation("Found {Count} completed/failed builds to delete", completed.Count);
        if (completed.Count == 0)
            return 0;

        _context.JobIndices.RemoveRange(completed);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Successfully deleted {Count} completed/failed builds", completed.Count);
        return completed.Count;
    }

    /// <summary>
    /// Read access, then the file of a build named in a request: no wildcard or empty path (400), and libddb's
    /// path rule (the same 400 as every request path).
    /// </summary>
    private async Task<(IDDB Ddb, string Path)> ResolveBuildFilePathAsync(string orgSlug, string dsSlug, string? path,
        CancellationToken ct)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        if (string.IsNullOrWhiteSpace(path) || path.Contains('*') || path.Contains('?'))
            throw new BadRequestException("Wildcards or empty paths are not supported");

        return (ddb, _paths.Normalize(path, "build file path"));
    }
}
