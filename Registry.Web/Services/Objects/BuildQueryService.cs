#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The builds of a dataset's entries: starting one, the files and downloadable artifacts libddb lists for
/// a build, the builds waiting for a dependency, and the record of finished build jobs.
/// </summary>
public sealed class BuildQueryService
{
    private readonly IDatasetAccessResolver _access;
    private readonly IBuildArtifactResolver _artifacts;
    private readonly DatasetPathPolicy _paths;
    private readonly BuildScheduler _builds;
    private readonly IJobIndexWriter _jobIndex;
    private readonly IUrlBuilder _urls;
    private readonly ILogger<BuildQueryService> _logger;

    public BuildQueryService(IDatasetAccessResolver access, IBuildArtifactResolver artifacts, DatasetPathPolicy paths,
        BuildScheduler builds, IJobIndexWriter jobIndex, IUrlBuilder urls, ILogger<BuildQueryService> logger)
    {
        _access = access;
        _artifacts = artifacts;
        _paths = paths;
        _builds = builds;
        _jobIndex = jobIndex;
        _urls = urls;
        _logger = logger;
    }

    /// <summary>
    /// Queues a build of <paramref name="path"/> (nothing when it is not buildable); <paramref name="force"/>
    /// builds again even while a build lock is held (a stale lock of a crashed build).
    /// </summary>
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

        var jobId = await _builds.QueueAsync(BuildTarget.Of(access, orgSlug, dsSlug), path, entry.Hash, force);
        _logger.LogInformation("Building '{Path}' asynchronously, background job id is {JobId}", path, jobId);
    }

    /// <summary>
    /// The file <paramref name="path"/> of the build <paramref name="hash"/>: its local path and, for a declared
    /// artifact, the manifest's media type when libddb gives it.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The file is not part of that build.</exception>
    public async Task<ResolvedBuildFile> GetBuildFileAsync(string orgSlug, string dsSlug, string hash, string? path,
        CancellationToken ct = default)
    {
        var (ddb, file) = await ResolveBuildFilePathAsync(orgSlug, dsSlug, path, ct);

        // libddb resolves the file of the build and checks that it stays inside it (DDBResolveBuildFile)
        return _artifacts.ResolveFile(ddb, hash, file)
               ?? throw new NotFoundException($"'{file}' is not a file of the build '{hash}'");
    }

    /// <summary>
    /// The file <paramref name="path"/> of the build <paramref name="hash"/>, or null when it is not a file of that
    /// build (a malformed hash names no build): <see cref="GetBuildFileAsync"/> without the 404.
    /// </summary>
    public async Task<ResolvedBuildFile?> FindBuildFileAsync(string orgSlug, string dsSlug, string hash, string? path,
        CancellationToken ct = default)
    {
        var (ddb, file) = await ResolveBuildFilePathAsync(orgSlug, dsSlug, path, ct);
        try
        {
            return _artifacts.ResolveFile(ddb, hash, file);
        }
        catch (DdbInvalidArgumentsException)
        {
            // A malformed hash names no build: the file does not exist (HEAD answers 404, as it always did)
            return null;
        }
    }

    /// <summary>
    /// The downloadable artifact of the entry at <paramref name="entryPath"/> (COG, COPC, GeoPackage), or with
    /// <paramref name="role"/> the existing downloadable artifact of that role (the <c>.spz</c> a splat is exported to).
    /// </summary>
    /// <exception cref="Exceptions.InvalidDatasetPathException">The path is not a dataset path (no path: empty).</exception>
    /// <exception cref="Exceptions.BadRequestException">The entry has no downloadable artifact, or its artifact of the
    /// role is not one.</exception>
    /// <exception cref="Exceptions.NotFoundException">No such entry, it is not built yet, its type has no artifact of the
    /// role, or the artifact of the role is made on demand and was not made.</exception>
    public async Task<BuildDownload> GetBuildArtifactAsync(string orgSlug, string dsSlug, string? entryPath,
        string? role = null, CancellationToken ct = default)
    {
        // libddb's path rule first (a refused path is a 400 whoever asks), then read access
        entryPath = _paths.Normalize(entryPath);
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        var entry = ddb.GetEntry(entryPath)
                    ?? throw new NotFoundException($"Entry '{entryPath}' does not exist in the dataset.");

        // The artifact libddb offers as a download (manifest: downloadName), served with Registry's media type
        var build = _artifacts.GetBuild(ddb, entry.Path);
        if (_artifacts.GetDownload(build, role) is { } download)
            return download;

        if (role != null)
            throw NoDownloadOfRole(entryPath, entry.Type, build, role);

        // A build that is complete without a downloadable artifact, or no build of its own: nothing to download
        if (!build.IsOwnBuild() || build.Build!.State == BuildState.Complete)
            throw new BadRequestException($"Entry type '{entry.Type}' does not have a downloadable build artifact.");

        throw new NotFoundException(
            $"Build artifact not found for '{entryPath}'. The entry may not have been built yet.");
    }

    /// <summary>
    /// Why the entry at <paramref name="entryPath"/> has no download of <paramref name="role"/>, from libddb's entry
    /// types and the entry's manifest: its type has no such role (or it has no build of its own), the artifact exists
    /// but is not offered as a download (no download name: the bounds of a splat), the entry is not built yet, or a
    /// complete build lacks it (a role made on demand, the <c>.spz</c> export, until a task made it).
    /// </summary>
    private Exception NoDownloadOfRole(string entryPath, EntryType type, EntryBuildArtifacts build, string role)
    {
        var declared = _artifacts.EntryTypes.Any(t => t.Id == type && t.Roles.Contains(role, StringComparer.Ordinal));
        if (!declared || !build.IsOwnBuild())
            return new NotFoundException($"Entry '{entryPath}' has no '{role}' artifact.");

        var own = build.Build!;
        if (own.Find(role) != null)
            return new BadRequestException($"The '{role}' artifact of entry '{entryPath}' cannot be downloaded.");

        if (own.State != BuildState.Complete)
            return new NotFoundException(
                $"Entry '{entryPath}' is not built yet: its '{role}' artifact exists once its build is complete.");

        return ArtifactRoles.MadeOnDemand.Contains(role)
            ? new NotFoundException(
                $"Entry '{entryPath}' has no '{role}' artifact yet. It is made on demand: run its export first.")
            : new NotFoundException($"Entry '{entryPath}' has no '{role}' artifact.");
    }

    /// <summary>The builds deferred for a missing dependency (companion file or external tool).</summary>
    public async Task<IEnumerable<PendingBuildInfoDto>> GetPendingBuildsAsync(string orgSlug, string dsSlug,
        CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        var pending = ddb.GetPendingBuildInfo();
        // The artifacts that exist (an earlier build's), one manifest call for every pending entry
        var builds = pending.Length == 0 ? [] : _artifacts.GetBuilds(ddb, [.. pending.Select(p => p.Path)]);
        return pending.Select((p, i) => new PendingBuildInfoDto
        {
            Path = p.Path,
            Hash = p.Hash,
            MissingDependencies = p.MissingDependencies,
            LastAttempt = DateTimeOffset.FromUnixTimeSeconds(p.LastAttempt).UtcDateTime,
            Artifacts = _urls.ArtifactLinks(orgSlug, dsSlug, builds[i]) is { Count: > 0 } links ? [.. links] : null
        }).ToArray();
    }

    /// <summary>Deletes the record of the build jobs that succeeded or failed; returns how many.</summary>
    public async Task<int> ClearCompletedBuildsAsync(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        // The job index's own rule for concluded jobs, which also refreshes the dataset's task list
        var removed = await _jobIndex.DeleteTerminalForDatasetAsync(orgSlug, dsSlug, ct: ct);

        _logger.LogInformation("Deleted {Count} concluded jobs of {Org}/{Ds}", removed.Count, orgSlug, dsSlug);
        return removed.Count;
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
