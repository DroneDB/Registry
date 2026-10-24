#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Objects;

/// <summary>
/// Default <see cref="IRasterProcessingService"/>. The required arguments are checked before the access check,
/// every path through libddb's rule; the native work runs on the heavy lane of <see cref="INativeWorkGate"/>.
/// </summary>
public sealed class RasterProcessingService : IRasterProcessingService
{
    private const string MaskBordersJob = "MaskBorders";

    private readonly IDatasetAccessResolver _access;
    private readonly DatasetPathPolicy _paths;
    private readonly IFileSystem _fs;
    private readonly INativeWorkGate _gate;
    private readonly EntryJobs _jobs;
    private readonly ILogger<RasterProcessingService> _logger;

    public RasterProcessingService(IDatasetAccessResolver access, DatasetPathPolicy paths, IFileSystem fs,
        INativeWorkGate gate, EntryJobs jobs, ILogger<RasterProcessingService> logger)
    {
        _access = access;
        _paths = paths;
        _fs = fs;
        _gate = gate;
        _jobs = jobs;
        _logger = logger;
    }

    public async Task<string> ValidateMergeMultispectralAsync(string orgSlug, string dsSlug, string[] paths,
        CancellationToken ct = default)
    {
        RequirePaths(paths);
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;
        paths = NormalizeAll(paths);

        return await _gate.RunAsync(NativeLane.Heavy, () => ddb.ValidateMergeMultispectral(paths), ct);
    }

    public async Task<byte[]> PreviewMergeMultispectralAsync(string orgSlug, string dsSlug, string[] paths,
        string? previewBands = null, int thumbSize = 512, CancellationToken ct = default)
    {
        RequirePaths(paths);
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;
        paths = NormalizeAll(paths);

        return await _gate.RunAsync(NativeLane.Heavy, () => ddb.PreviewMergeMultispectral(paths, previewBands, thumbSize),
            ct);
    }

    public async Task MergeMultispectralAsync(string orgSlug, string dsSlug, string[] paths, string? outputPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new BadRequestException("outputPath is required");
        RequirePaths(paths);

        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct)).Ddb;

        outputPath = _paths.Normalize(outputPath, "output path");
        paths = NormalizeAll(paths);

        var outputFullPath = ddb.GetLocalPath(outputPath);
        if (_fs.RemoveEntry(outputFullPath, isFolder: false))
            _logger.LogInformation("Output file already existed, deleted: '{OutputPath}'", outputFullPath);

        _logger.LogInformation("Merging multispectral for '{OutputPath}'", outputFullPath);
        await _gate.RunAsync(NativeLane.Heavy, () => ddb.MergeMultispectral(paths, outputPath), ct);

        ddb.AddRaw(outputPath);
        _logger.LogInformation("Merge complete and file added to the dataset");
    }

    public async Task<string> ValidateAlignRasterAsync(string orgSlug, string dsSlug, string? sourcePath,
        string? referencePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new BadRequestException("sourcePath is required");
        if (string.IsNullOrWhiteSpace(referencePath))
            throw new BadRequestException("referencePath is required");

        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        sourcePath = _paths.Normalize(sourcePath, "source path");
        referencePath = _paths.Normalize(referencePath, "reference path");

        return await _gate.RunAsync(NativeLane.Heavy, () => ddb.ValidateAlignRaster(sourcePath, referencePath), ct);
    }

    public async Task<MaskBordersCheckResponseDto> CheckMaskedFileExistsAsync(string orgSlug, string dsSlug,
        string path, CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        var outputPath = MaskedOutputPath(_paths.Normalize(path));
        return new MaskBordersCheckResponseDto { Exists = ddb.GetEntry(outputPath) != null, OutputPath = outputPath };
    }

    public async Task MaskBordersAsync(string orgSlug, string dsSlug, string path, int nearDist, bool white,
        CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        await _jobs.EnsureNoActiveJobAsync(orgSlug, dsSlug, path, MaskBordersJob, ct);

        var ddb = access.Ddb;
        path = _paths.Normalize(path);

        var entry = ddb.GetEntry(path) ?? throw new NotFoundException($"Cannot find entry: '{path}'");
        if (entry.Type != EntryType.GeoRaster)
            throw new BadRequestException($"Entry '{path}' is not a georaster");

        var outputPath = MaskedOutputPath(path);
        var outputFullPath = ddb.GetLocalPath(outputPath);
        if (_fs.RemoveEntry(outputFullPath, isFolder: false))
            _logger.LogInformation("Output file already existed, deleted: '{OutputPath}'", outputFullPath);

        var jobId = await _jobs.EnqueueMaskBordersAsync(access, orgSlug, dsSlug, entry, outputPath, nearDist, white);
        _logger.LogInformation("Masking borders for '{Path}' asynchronously, background job id is {JobId}", path, jobId);
    }

    // The masked copy of a normalized path, next to it
    private static string MaskedOutputPath(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path) + "_masked.tif";
        var slash = path.LastIndexOf('/');
        return slash < 0 ? fileName : path[..(slash + 1)] + fileName;
    }

    private static void RequirePaths(string[]? paths)
    {
        if (paths == null || paths.Length == 0)
            throw new BadRequestException("paths is required");
    }

    private string[] NormalizeAll(string[] paths) => Array.ConvertAll(paths, p => _paths.Normalize(p));
}
