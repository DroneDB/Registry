#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Objects;

/// <summary>Renders of dataset entries: thumbnails and map tiles, plain or with visualization parameters.</summary>
public interface IObjectRenderService
{
    /// <summary>
    /// Checks read access and resolves <paramref name="path"/> to its entry and render source, without
    /// rendering anything (a caller can answer a conditional request first).
    /// </summary>
    Task<ReadableObject> ResolveReadableObjectAsync(string orgSlug, string dsSlug, string? path,
        CancellationToken ct = default);

    /// <summary>
    /// The thumbnail of <paramref name="path"/>, or the dataset's thumbnail when it is null or blank (null when
    /// the dataset has no thumbnail candidate). <paramref name="recreate"/> drops the cached one first.
    /// </summary>
    Task<StorageDataDto?> GenerateThumbnailDataAsync(string orgSlug, string dsSlug, string? path, int? size,
        bool recreate = false, CancellationToken ct = default);

    /// <summary>The thumbnail of an entry resolved by <see cref="ResolveReadableObjectAsync"/>.</summary>
    Task<StorageDataDto> GenerateThumbnailDataAsync(ReadableObject obj, int? size, CancellationToken ct = default);

    /// <summary>The map tile of <paramref name="path"/>.</summary>
    Task<StorageDataDto> GenerateTileDataAsync(string orgSlug, string dsSlug, string? path, int tz, int tx, int ty,
        bool retina, CancellationToken ct = default);

    /// <summary>The map tile of an entry resolved by <see cref="ResolveReadableObjectAsync"/>.</summary>
    Task<StorageDataDto> GenerateTileDataAsync(ReadableObject obj, int tz, int tx, int ty, bool retina,
        CancellationToken ct = default);

    /// <summary>The thumbnail of a raster with visualization parameters (preset, bands, formula, colormap, ...).</summary>
    Task<StorageDataDto> GenerateThumbnailDataExAsync(string orgSlug, string dsSlug, string path, int? size,
        RasterVisualization visualization, CancellationToken ct = default);

    /// <summary>The map tile of a raster with visualization parameters.</summary>
    Task<StorageDataDto> GenerateTileDataExAsync(string orgSlug, string dsSlug, string path, int tz, int tx, int ty,
        bool retina, RasterVisualization visualization, CancellationToken ct = default);
}

/// <summary>
/// How a raster is shown: a sensor preset or explicit bands, a band formula with its band filter, a
/// colormap and a rescale range (null: the default of each).
/// </summary>
public sealed record RasterVisualization(
    string? Preset = null, string? Bands = null, string? Formula = null, string? BandFilter = null,
    string? Colormap = null, string? Rescale = null)
{
    /// <summary>The cache key part of these parameters.</summary>
    public string CacheKey => $"{Preset}|{Bands}|{Formula}|{BandFilter}|{Colormap}|{Rescale}";
}
