using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Objects;

/// <summary>
/// Raster operations that make new entries: single-band rasters merged into one multi-band raster (with a
/// check and a preview first), the alignment check of two rasters, and the masking of orthophoto borders.
/// </summary>
public sealed class RasterProcessingService
{
    private const string MaskBordersJob = "MaskBorders";

    private readonly IDatasetAccessResolver _access;
    private readonly DatasetPathPolicy _paths;
    private readonly INativeWorkGate _gate;
    private readonly EntryJobs _jobs;
    private readonly IStorageQuota _quota;
    private readonly ILogger<RasterProcessingService> _logger;

    public RasterProcessingService(IDatasetAccessResolver access, DatasetPathPolicy paths,
        INativeWorkGate gate, EntryJobs jobs, IStorageQuota quota, ILogger<RasterProcessingService> logger)
    {
        _quota = quota;
        _access = access;
        _paths = paths;
        _gate = gate;
        _jobs = jobs;
        _logger = logger;
    }

    /// <summary>libddb's check of the inputs of a merge (JSON).</summary>
    public async Task<string> ValidateMergeMultispectralAsync(string orgSlug, string dsSlug, string[] paths,
        CancellationToken ct = default)
    {
        RequirePaths(paths);
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;
        paths = NormalizeAll(paths);

        return await _gate.RunAsync(NativeLane.Heavy, () => ddb.ValidateMergeMultispectral(paths), ct);
    }

    /// <summary>A preview image of the merge (webp).</summary>
    public async Task<byte[]> PreviewMergeMultispectralAsync(string orgSlug, string dsSlug, string[] paths,
        string? previewBands = null, int thumbSize = 512, CancellationToken ct = default)
    {
        RequirePaths(paths);
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;
        paths = NormalizeAll(paths);

        return await _gate.RunAsync(NativeLane.Heavy, () => ddb.PreviewMergeMultispectral(paths, previewBands, thumbSize),
            ct);
    }

    /// <summary>
    /// Merges the single-band <paramref name="paths"/> into <paramref name="outputPath"/> and indexes it. An existing
    /// output is replaced only with <paramref name="overwrite"/> (D-040); an output that is one of the bands never is.
    /// </summary>
    /// <exception cref="BadRequestException">A path is missing, or the output is one of the bands.</exception>
    /// <exception cref="NameClashException">The output exists and <paramref name="overwrite"/> is false, or it did not
    /// exist at the request and another writer created it before the merge wrote it.</exception>
    public async Task MergeMultispectralAsync(string orgSlug, string dsSlug, string[] paths, string? outputPath,
        bool overwrite = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new BadRequestException("outputPath is required");
        RequirePaths(paths);

        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);
        var ddb = access.Ddb;

        outputPath = _paths.Normalize(outputPath, "output path");
        paths = NormalizeAll(paths);

        // The rules of every tool output: not one of the inputs (400), an existing file only when asked (409)
        HeavyToolPaths.EnsureOutputsAreNotInputs(paths, [outputPath]);
        outputPath = DatasetEntryLookup.ResolveOutput(ddb, outputPath, overwrite);
        await EnsureOwnerHasStorageLeftAsync(access.Dataset, ct);

        // libddb replaces an existing output only when asked, once the merge is complete: a failed merge leaves it as
        // it was (FR-PRC-01). The replace is decided now, for the file that is there: an output that did not exist
        // and appears while the merge waits or runs (another writer) is refused by libddb, never replaced
        var outputFullPath = ddb.GetLocalPath(outputPath);
        var replacing = overwrite && File.Exists(outputFullPath);

        _logger.LogInformation("Merging multispectral for '{OutputPath}'", outputPath);
        try
        {
            await _gate.RunAsync(NativeLane.Heavy, () => ddb.MergeMultispectral(paths, outputPath, replacing), ct);
        }
        catch (DdbException) when (!replacing && File.Exists(outputFullPath))
        {
            // Created by another writer since the request: kept, and refused as any existing output (409)
            throw new NameClashException(outputPath);
        }

        ddb.AddRaw(outputPath);
        _logger.LogInformation("Merge complete and file added to the dataset");
    }

    /// <summary>libddb's check that two rasters can be aligned (JSON).</summary>
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

    /// <summary>Whether the masked copy of <paramref name="path"/> (next to it) exists, in any letter case (D-040).</summary>
    public async Task<MaskBordersCheckResponseDto> CheckMaskedFileExistsAsync(string orgSlug, string dsSlug,
        string path, CancellationToken ct = default)
    {
        var ddb = (await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct)).Ddb;

        var outputPath = MaskedOutputPath(_paths.Normalize(path));
        var existing = DatasetEntryLookup.FindExisting(ddb, outputPath);
        return new MaskBordersCheckResponseDto { Exists = existing != null, OutputPath = existing?.Path ?? outputPath };
    }

    /// <summary>
    /// Starts the job that writes the masked copy of the georaster <paramref name="path"/>. An existing copy is
    /// replaced only with <paramref name="overwrite"/> (D-040).
    /// </summary>
    /// <exception cref="NameClashException">The copy exists and <paramref name="overwrite"/> is false.</exception>
    public async Task MaskBordersAsync(string orgSlug, string dsSlug, string path, int nearDist, bool white,
        bool overwrite = false, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        var ddb = access.Ddb;
        path = _paths.Normalize(path);

        // One job per source, and none while an unfinished task writes the masked copy (the Mask Borders tool): 409
        await _jobs.EnsureNoActiveJobAsync(orgSlug, dsSlug, path, MaskBordersJob, MaskedOutputPath(path), ct);

        var entry = ddb.GetEntry(path) ?? throw new NotFoundException($"Cannot find entry: '{path}'");
        if (entry.Type != EntryType.GeoRaster)
            throw new BadRequestException($"Entry '{path}' is not a georaster");

        // A name that differs only in letter case is the existing copy (D-040)
        var outputPath = DatasetEntryLookup.ResolveOutput(ddb, MaskedOutputPath(path), overwrite);
        await EnsureOwnerHasStorageLeftAsync(access.Dataset, ct);

        // The job replaces an existing copy only when asked, once the mask is complete (libddb): a failed job leaves
        // it as it was (FR-PRC-01). Only a copy that exists now may be replaced: without one, the job runs without the
        // replace and refuses a file that another writer creates while it waits (409)
        var replaces = overwrite && DatasetEntryLookup.FindExisting(ddb, outputPath) != null;
        var jobId = await _jobs.EnqueueMaskBordersAsync(access, orgSlug, dsSlug, entry, outputPath, nearDist, white,
            replaces);
        _logger.LogInformation("Masking borders for '{Path}' asynchronously, background job id is {JobId}", path, jobId);
    }

    // A write of a file into the dataset: the owner of its organization (whoever writes, D-039) must have storage left
    // (FR-XC-26, SEC-003), else 507, before anything is deleted or started. The size of the output is not known before
    // the native step writes it
    private async Task EnsureOwnerHasStorageLeftAsync(Registry.Web.Data.Models.Dataset dataset, CancellationToken ct)
    {
        if (await _quota.RemainingBytesAsync(dataset.Organization?.OwnerId, ct) is { } left && left <= 0)
            throw QuotaExceededException.OwnerOverQuota(left);
    }

    // The masked copy of a normalized path, next to it (the rule of the mask-borders tool)
    private static string MaskedOutputPath(string path) => Registry.Web.Services.HeavyTasks.Tools.MaskBordersTool.MaskedOutputPath(path);

    private static void RequirePaths(string[]? paths)
    {
        if (paths == null || paths.Length == 0)
            throw new BadRequestException("paths is required");
    }

    private string[] NormalizeAll(string[] paths) => Array.ConvertAll(paths, p => _paths.Normalize(p));
}
