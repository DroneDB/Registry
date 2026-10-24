#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The file a read request renders (thumbnails, tiles, raster analysis): read access first (an anonymous
/// caller learns nothing about paths), then the path (libddb's rule, no wildcard, an existing entry that is
/// not a folder: 400 otherwise), for a thumbnail the type's rule (libddb: some types have none, 404), then the
/// file a renderer reads (its build artifact from libddb's manifest, or the entry itself). A missing artifact is
/// a 404 "not available yet", decided before any response cache, so deleting an artifact invalidates what was
/// served from it.
/// </summary>
public sealed class RenderSources
{
    private readonly IDatasetAccessResolver _access;
    private readonly IBuildArtifactResolver _artifacts;
    private readonly IFileSystem _fs;
    private readonly ILogger<RenderSources> _logger;

    public RenderSources(IDatasetAccessResolver access, IBuildArtifactResolver artifacts, IFileSystem fs,
        ILogger<RenderSources> logger)
    {
        _access = access;
        _artifacts = artifacts;
        _fs = fs;
        _logger = logger;
    }

    /// <summary>The dataset's index, after checking read access (the dataset thumbnail).</summary>
    public async Task<IDDB> OpenReadableAsync(string orgSlug, string dsSlug, CancellationToken ct = default) =>
        (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

    /// <summary>The entry at <paramref name="path"/> and the file a renderer reads for it.</summary>
    /// <exception cref="InvalidDatasetPathException">The path is not a dataset path (a missing one is empty).</exception>
    /// <exception cref="BadRequestException">A wildcard, no such entry, or a folder.</exception>
    /// <exception cref="NotFoundException">The build artifact a renderer reads does not exist yet.</exception>
    public Task<RenderSource> ResolveAsync(string orgSlug, string dsSlug, string? path,
        CancellationToken ct = default) =>
        ResolveAsync(orgSlug, dsSlug, path, thumbnail: false, ct);

    private async Task<RenderSource> ResolveAsync(string orgSlug, string dsSlug, string? path, bool thumbnail,
        CancellationToken ct)
    {
        // A render names an entry: no path is the empty path (400 with the path rule's message)
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, path ?? string.Empty, ct);
        var normalized = access.Path!;

        if (normalized.Contains('*') || normalized.Contains('?'))
            throw new BadRequestException("Wildcards or empty paths are not supported");

        var entry = access.Entry ?? throw new BadRequestException($"Invalid path: '{normalized}'");

        // Every caller reads the file content (thumbnails, tiles, raster operations)
        if (entry.Type == EntryType.Directory)
            throw new BadRequestException($"'{normalized}' is a folder");

        // libddb's rule, before any native work: a type without a thumbnail has nothing a renderer could open
        if (thumbnail && !_artifacts.HasThumbnail(entry.Type))
            throw new NotFoundException($"'{normalized}' has no thumbnail ({entry.Type} entries have none)");

        return new RenderSource(entry, access.Ddb, normalized, SourceOf(entry, access.Ddb, normalized));
    }

    /// <summary>
    /// <see cref="ResolveAsync(string, string, string?, CancellationToken)"/> with the version of the rendered file: a rebuild rewrites the artifact
    /// but keeps the entry hash, its write time tells them apart.
    /// </summary>
    public Task<ReadableObject> ResolveReadableAsync(string orgSlug, string dsSlug, string? path,
        CancellationToken ct = default) =>
        ResolveReadableAsync(orgSlug, dsSlug, path, thumbnail: false, ct);

    /// <summary><see cref="ResolveReadableAsync(string, string, string?, CancellationToken)"/> for a thumbnail.</summary>
    /// <exception cref="NotFoundException">The entry's type has no thumbnail, or its artifact does not exist yet.</exception>
    public Task<ReadableObject> ResolveThumbnailAsync(string orgSlug, string dsSlug, string? path,
        CancellationToken ct = default) =>
        ResolveReadableAsync(orgSlug, dsSlug, path, thumbnail: true, ct);

    private async Task<ReadableObject> ResolveReadableAsync(string orgSlug, string dsSlug, string? path,
        bool thumbnail, CancellationToken ct)
    {
        var source = await ResolveAsync(orgSlug, dsSlug, path, thumbnail, ct);

        long version = 0;
        try
        {
            version = _fs.GetLastWriteTime(source.Ddb.GetLocalPath(source.SourcePath)).ToUniversalTime().Ticks;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cannot read the write time of '{SourcePath}'", source.SourcePath);
        }

        return new ReadableObject(orgSlug, dsSlug, source.Path, source.Entry.Hash, source.SourcePath, source.Ddb,
            version);
    }

    private string SourceOf(Entry entry, IDDB ddb, string requestPath)
    {
        var sourcePath = _artifacts.GetRenderSource(ddb, entry);
        if (sourcePath != null)
            return sourcePath;

        _logger.LogInformation("Build artifact for '{OriginalPath}' (hash {Hash}) not available yet",
            requestPath, entry.Hash);
        throw new NotFoundException(
            $"Processed data for '{requestPath}' is not available yet (build pending or failed)");
    }
}

/// <summary>The entry of a read request, its index, the normalized request path and the file to read.</summary>
public sealed record RenderSource(Entry Entry, IDDB Ddb, string Path, string SourcePath);
