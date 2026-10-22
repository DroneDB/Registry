#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Objects;

/// <summary>
/// Raster operations that make new entries: single-band rasters merged into one multi-band raster (with a
/// check and a preview first), the alignment check of two rasters, and the masking of orthophoto borders.
/// </summary>
public interface IRasterProcessingService
{
    /// <summary>libddb's check of the inputs of a merge (JSON).</summary>
    Task<string> ValidateMergeMultispectralAsync(string orgSlug, string dsSlug, string[] paths,
        CancellationToken ct = default);

    /// <summary>A preview image of the merge (webp).</summary>
    Task<byte[]> PreviewMergeMultispectralAsync(string orgSlug, string dsSlug, string[] paths,
        string? previewBands = null, int thumbSize = 512, CancellationToken ct = default);

    /// <summary>Merges the single-band <paramref name="paths"/> into <paramref name="outputPath"/> (replaced) and indexes it.</summary>
    Task MergeMultispectralAsync(string orgSlug, string dsSlug, string[] paths, string? outputPath,
        CancellationToken ct = default);

    /// <summary>libddb's check that two rasters can be aligned (JSON).</summary>
    Task<string> ValidateAlignRasterAsync(string orgSlug, string dsSlug, string? sourcePath, string? referencePath,
        CancellationToken ct = default);

    /// <summary>Whether the masked copy of <paramref name="path"/> (next to it) exists.</summary>
    Task<MaskBordersCheckResponseDto> CheckMaskedFileExistsAsync(string orgSlug, string dsSlug, string path,
        CancellationToken ct = default);

    /// <summary>Starts the job that writes the masked copy of the georaster <paramref name="path"/> (replaced).</summary>
    Task MaskBordersAsync(string orgSlug, string dsSlug, string path, int nearDist, bool white,
        CancellationToken ct = default);
}
