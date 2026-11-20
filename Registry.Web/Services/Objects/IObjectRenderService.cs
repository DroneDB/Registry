using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Utilities;

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
    /// <see cref="ResolveReadableObjectAsync"/> for a thumbnail: an entry whose type has none (libddb's entry
    /// types: models, videos, panoramas, vectors, ...) is a 404 before any native work.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The type has no thumbnail, or its artifact is not built yet.</exception>
    Task<ReadableObject> ResolveThumbnailObjectAsync(string orgSlug, string dsSlug, string? path,
        CancellationToken ct = default);

    /// <summary>
    /// The thumbnail of <paramref name="path"/>, or the dataset's thumbnail when it is null or blank (null when
    /// the dataset has no thumbnail candidate). <paramref name="recreate"/> drops the cached one first.
    /// </summary>
    Task<StorageDataDto?> GenerateThumbnailDataAsync(string orgSlug, string dsSlug, string? path, int? size,
        bool recreate = false, CancellationToken ct = default);

    /// <summary>The thumbnail of an entry resolved by <see cref="ResolveThumbnailObjectAsync"/>.</summary>
    Task<StorageDataDto> GenerateThumbnailDataAsync(ReadableObject obj, int? size, CancellationToken ct = default);

    /// <summary>The map tile of an entry resolved by <see cref="ResolveReadableObjectAsync"/>.</summary>
    Task<StorageDataDto> GenerateTileDataAsync(ReadableObject obj, int tz, int tx, int ty, bool retina,
        TileFormat format, CancellationToken ct = default);

    /// <summary>The thumbnail of a raster with visualization parameters (preset, bands, formula, colormap, ...).</summary>
    Task<StorageDataDto> GenerateThumbnailDataExAsync(string orgSlug, string dsSlug, string path, int? size,
        RasterVisualization visualization, CancellationToken ct = default);

    /// <summary>The map tile of a raster with visualization parameters, as an image in <paramref name="format"/>.</summary>
    Task<StorageDataDto> GenerateTileDataExAsync(string orgSlug, string dsSlug, string path, int tz, int tx, int ty,
        bool retina, RasterVisualization visualization, TileFormat format, CancellationToken ct = default);
}

/// <summary>
/// How a raster is shown: a sensor preset or explicit bands, a band formula with its band filter, a
/// colormap and a rescale range (null: the default of each), a NoData value that replaces the one of
/// the bands (FR-RAS-31: a pixel equal to it is transparent and out of the stretch), and the temperature unit the
/// rescale of a thermal formula is written in (D-022: libddb converts it to the unit of the formula,
/// <see cref="RasterOptionRules.RescaleUnit"/>).
/// </summary>
public sealed record RasterVisualization(
    string? Preset = null, string? Bands = null, string? Formula = null, string? BandFilter = null,
    string? Colormap = null, string? Rescale = null, double? Nodata = null, string? RescaleUnit = null)
{
    /// <summary>The unit of the rescale, trimmed; null when blank.</summary>
    public string? RescaleUnit { get; init; } = string.IsNullOrWhiteSpace(RescaleUnit) ? null : RescaleUnit.Trim();

    /// <summary>
    /// The options of the render (the NoData value and the unit of the rescale), or null when it has neither: the render
    /// then uses the export without options.
    /// </summary>
    public Registry.Ports.DroneDB.RasterAnalysisOptions? Options => Nodata is null && RescaleUnit is null
        ? null
        : new Registry.Ports.DroneDB.RasterAnalysisOptions(Nodata, RescaleUnit: RescaleUnit);

    /// <summary>
    /// The rescale range, <c>min,max</c> with '.' as the decimal sign. A value that is not two finite numbers, or whose min is
    /// not less than its max, is a 400 at the <c>rescale</c> parameter, not a failure of the native render (FR-RAS-10).
    /// </summary>
    /// <exception cref="InvalidFieldException">The value is not a valid range.</exception>
    public string? Rescale { get; init; } = RescaleRange.Validated(Rescale);

    /// <summary>The cache key part of these parameters.</summary>
    /// <remarks>The unit of the rescale is added only when it is set, so the keys of the renders without it stay as they were.</remarks>
    public string CacheKey => $"{Preset}|{Bands}|{Formula}|{BandFilter}|{Colormap}|{Rescale}|{NodataKey}" +
                              (RescaleUnit is null ? string.Empty : $"|{RescaleUnit}");

    private string NodataKey => Nodata is { } nodata ? nodata.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
}
