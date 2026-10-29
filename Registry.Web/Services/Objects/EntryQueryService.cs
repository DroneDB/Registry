using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeMapping;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Objects;

/// <summary>Default <see cref="IEntryQueryService"/>.</summary>
public sealed class EntryQueryService : IEntryQueryService
{
    private readonly IDatasetAccessResolver _access;
    private readonly IBuildStatusService _buildStatus;
    private readonly IZipArchiveBuilder _zip;
    private readonly IAuthManager _authManager;
    private readonly DownloadOptions _downloads;
    private readonly ILogger<EntryQueryService> _logger;

    public EntryQueryService(IDatasetAccessResolver access, IBuildStatusService buildStatus, IZipArchiveBuilder zip,
        IAuthManager authManager, IOptions<DownloadOptions> downloads, ILogger<EntryQueryService> logger)
    {
        _access = access;
        _buildStatus = buildStatus;
        _zip = zip;
        _authManager = authManager;
        _downloads = downloads.Value;
        _logger = logger;
    }

    public async Task<IEnumerable<EntryDto>> ListAsync(string orgSlug, string dsSlug, string? path = null,
        bool recursive = false, EntryType? type = null, CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        _logger.LogInformation("Listing '{OrgSlug}/{DsSlug}' in '{Path}'", orgSlug, dsSlug, path);

        var files = ddb.Find(new EntryQuery(path, recursive, Types: TypesOf(type))).Select(item => item.ToDto())
            .ToArray();

        await _buildStatus.AnnotateAsync(orgSlug, dsSlug, ddb, files);

        _logger.LogInformation("Found {FilesCount} objects", files.Length);
        return files;
    }

    public async Task<IEnumerable<EntryDto>> SearchAsync(string orgSlug, string dsSlug, string? query = null,
        string? path = null, bool recursive = true, EntryType? type = null, CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        _logger.LogInformation("Searching '{OrgSlug}/{DsSlug}' in '{Path}' -> {Query} ({Recursive})", orgSlug, dsSlug,
            path, query, recursive ? 'r' : 'n');

        // DroneDB selects by folder, name and type; the expression check keeps the exact
        // FileSystemName rules on what it returns. The native pattern ignores case only for ASCII
        // letters and has no escapes, so other queries are matched in managed code only.
        var entities = ddb.Find(new EntryQuery(path, recursive, NativeNamePattern(query), TypesOf(type)));

        var files = (from entry in entities
            let name = Path.GetFileName(entry.Path)
            where FileSystemName.MatchesSimpleExpression(query, name)
            select entry.ToDto()).ToArray();

        await _buildStatus.AnnotateAsync(orgSlug, dsSlug, ddb, files);

        _logger.LogInformation("Found {FilesCount} objects", files.Length);
        return files;
    }

    public async Task<StorageEntryDto> GetAsync(string orgSlug, string dsSlug, string? path, CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        if (string.IsNullOrWhiteSpace(path))
            throw new BadRequestException("Path should not be null");

        var entry = ddb.GetEntry(path) ?? throw new NotFoundException($"Cannot find '{path}'");

        if (entry.Type == EntryType.Directory)
            throw new BadRequestException("Cannot get a folder, we are supposed to deal with a file!");

        return new StorageEntryDto
        {
            Hash = entry.Hash,
            Name = Path.GetFileName(entry.Path),
            Size = entry.Size,
            Type = entry.Type,
            ContentType = MimeUtility.GetMimeMapping(entry.Path),
            PhysicalPath = Path.GetFullPath(ddb.GetLocalPath(entry.Path))
        };
    }

    public async Task<EntryType?> GetEntryTypeAsync(string orgSlug, string dsSlug, string path,
        CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;
        return ddb.GetEntry(path)?.Type;
    }

    public async Task<FileStreamDescriptor> DownloadAsync(string orgSlug, string dsSlug, string[]? paths,
        CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        _logger.LogInformation("Download from '{OrgSlug}/{DsSlug}'", orgSlug, dsSlug);

        EnsurePathsValidity(ddb, paths);

        var (files, folders, includeDdb) = _zip.ExpandPaths(ddb, paths);

        // One file asked for by its own path: the file itself; anything else: a ZIP archive
        var descriptor = files.Length == 1 && paths?.Length == 1 && files[0] == paths[0]
            ? new FileStreamDescriptor(Path.GetFileName(files[0]), MimeUtility.GetMimeMapping(files[0]), ddb, files,
                null, FileDescriptorType.Single, _logger, _zip)
            : new FileStreamDescriptor($"{orgSlug}-{dsSlug}-{CommonUtils.RandomString(8)}.zip", "application/zip", ddb,
                files, folders, includeDdb ? FileDescriptorType.Dataset : FileDescriptorType.Multiple, _logger, _zip);

        // Anonymous users cannot download bulk archives (the whole dataset, folders, several files) when
        // the policy says so; single files stay allowed
        if (_downloads.DisableAnonymousBulkDownloads && descriptor.Type != FileDescriptorType.Single &&
            await _authManager.GetCurrentUser() == null)
        {
            _logger.LogWarning("Blocked anonymous bulk download for '{OrgSlug}/{DsSlug}' (type={Type}, paths='{Paths}')",
                orgSlug, dsSlug, descriptor.Type, paths != null ? string.Join(",", paths) : "(none)");
            throw new UnauthorizedException(
                "Bulk downloads are not allowed for anonymous users. Please log in to download multiple files or entire datasets.");
        }

        return descriptor;
    }

    public async Task<FileStreamDescriptor> GetDdbAsync(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        return new FileStreamDescriptor($"{orgSlug}-{dsSlug}-ddb.zip", "application/zip", ddb, [], [],
            FileDescriptorType.Dataset, _logger, _zip);
    }

    private static EntryType[]? TypesOf(EntryType? type) => type is { } t ? [t] : null;

    /// <summary>
    /// The query as a DroneDB name pattern when DroneDB selects at least every name that
    /// <see cref="FileSystemName.MatchesSimpleExpression(ReadOnlySpan{char}, ReadOnlySpan{char}, bool)"/>
    /// accepts (ASCII only, no '\' escapes), otherwise null.
    /// </summary>
    private static string? NativeNamePattern(string? query) =>
        !string.IsNullOrEmpty(query) && query.All(c => c < 128 && c != '\\') ? query : null;

    // No paths: everything. Otherwise distinct existing entries, no wildcard
    private static void EnsurePathsValidity(IDDB ddb, string[]? paths)
    {
        if (paths == null || paths.Length == 0)
            return;

        if (paths.Any(path => path.Contains('*') || path.Contains('?') || string.IsNullOrWhiteSpace(path)))
            throw new BadRequestException("Wildcards or empty paths are not supported");

        if (paths.Length != paths.Distinct().Count())
            throw new BadRequestException("Duplicate paths");

        foreach (var path in paths)
            if (!ddb.EntryExists(path))
                throw new BadRequestException($"Invalid path: '{path}'");
    }
}
