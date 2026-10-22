#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeMapping;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using static Registry.Web.Services.CacheCategories;

namespace Registry.Web.Services.Objects;

/// <summary>
/// Default <see cref="IObjectRenderService"/>: renders are cached per entry hash (the dataset thumbnail per
/// candidate path) and every native render runs through <see cref="INativeWorkGate"/> (tiles on the tiles
/// lane, thumbnails on the heavy lane, through the gated thumbnail generator).
/// </summary>
public sealed class ObjectRenderService : IObjectRenderService
{
    private const string TileMediaType = "image/webp";

    private readonly RenderSources _sources;
    private readonly ICacheManager _cache;
    private readonly IThumbnailGenerator _thumbnails;
    private readonly IDdbWrapper _ddbWrapper;
    private readonly INativeWorkGate _gate;
    private readonly ThumbnailOptions _options;
    private readonly ILogger<ObjectRenderService> _logger;

    public ObjectRenderService(RenderSources sources, ICacheManager cache, IThumbnailGenerator thumbnails,
        IDdbWrapper ddbWrapper, INativeWorkGate gate, IOptions<ThumbnailOptions> options,
        ILogger<ObjectRenderService> logger)
    {
        _sources = sources;
        _cache = cache;
        _thumbnails = thumbnails;
        _ddbWrapper = ddbWrapper;
        _gate = gate;
        _options = options.Value;
        _logger = logger;
    }

    public Task<ReadableObject> ResolveReadableObjectAsync(string orgSlug, string dsSlug, string? path,
        CancellationToken ct = default) =>
        _sources.ResolveReadableAsync(orgSlug, dsSlug, path, ct);

    public async Task<StorageDataDto?> GenerateThumbnailDataAsync(string orgSlug, string dsSlug, string? path,
        int? size, bool recreate = false, CancellationToken ct = default)
    {
        _logger.LogInformation("Thumbnail of '{OrgSlug}/{DsSlug}', path: '{Path}', size: {Size}, recreate: {Recreate}",
            orgSlug, dsSlug, path, size, recreate);

        // Dataset thumbnail mode: path is null or empty
        if (string.IsNullOrWhiteSpace(path))
            return await GenerateDatasetThumbnailDataAsync(orgSlug, dsSlug, size, recreate, ct);

        var obj = await _sources.ResolveReadableAsync(orgSlug, dsSlug, path, ct);

        if (recreate)
        {
            var pixels = size ?? _options.DefaultThumbnailSize;
            _logger.LogDebug("Removing cached thumbnail for {OrgSlug}/{DsSlug}, hash: {Hash}, size: {Size}",
                orgSlug, dsSlug, obj.Hash, pixels);
            // Same key parts as the read below, otherwise nothing is removed
            await _cache.RemoveAsync(MagicStrings.ThumbnailCacheSeed, ForDataset(orgSlug, dsSlug), obj.Hash, pixels);
        }

        return await GenerateThumbnailDataAsync(obj, size, ct);
    }

    public async Task<StorageDataDto> GenerateThumbnailDataAsync(ReadableObject obj, int? size,
        CancellationToken ct = default)
    {
        var localPath = obj.Ddb.GetLocalPath(obj.SourcePath);
        var pixels = size ?? _options.DefaultThumbnailSize;

        var data = await _cache.GetAsync(MagicStrings.ThumbnailCacheSeed, ForDataset(obj.OrgSlug, obj.DsSlug),
            obj.Hash, pixels, new Func<Task<byte[]>>(() => RenderThumbnailAsync(localPath, pixels)));

        var extension = MimeUtility.GetExtensions(_ddbWrapper.ThumbnailMimeType)?.FirstOrDefault() ?? "webp";

        return new StorageDataDto
        {
            Name = Path.ChangeExtension(Path.GetFileName(obj.Path), extension),
            Data = data,
            ContentType = _ddbWrapper.ThumbnailMimeType
        };
    }

    public async Task<StorageDataDto> GenerateTileDataAsync(string orgSlug, string dsSlug, string? path, int tz,
        int tx, int ty, bool retina, CancellationToken ct = default) =>
        await GenerateTileDataAsync(await _sources.ResolveReadableAsync(orgSlug, dsSlug, path, ct), tz, tx, ty,
            retina, ct);

    public Task<StorageDataDto> GenerateTileDataAsync(ReadableObject obj, int tz, int tx, int ty, bool retina,
        CancellationToken ct = default) =>
        CachedTileAsync(ty, () => _cache.GetAsync(MagicStrings.TileCacheSeed, ForDataset(obj.OrgSlug, obj.DsSlug),
            obj.Hash, tx, ty, tz, retina,
            new Func<Task<byte[]>>(() => _gate.RunAsync(NativeLane.Tiles,
                () => obj.Ddb.GenerateTile(obj.SourcePath, tz, tx, ty, retina, obj.Hash, "webp"), ct))));

    public async Task<StorageDataDto> GenerateThumbnailDataExAsync(string orgSlug, string dsSlug, string path,
        int? size, RasterVisualization visualization, CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        var pixels = size ?? _options.DefaultThumbnailSize;
        var v = visualization;

        var data = await _cache.GetAsync(MagicStrings.ThumbnailCacheSeed, ForDataset(orgSlug, dsSlug),
            source.Entry.Hash, pixels, v.CacheKey,
            new Func<Task<byte[]>>(() => _gate.RunAsync(NativeLane.Heavy, () => source.Ddb.GenerateThumbnailEx(
                source.SourcePath, pixels, v.Preset, v.Bands, v.Formula, v.BandFilter, v.Colormap, v.Rescale), ct)));

        return new StorageDataDto
        {
            Name = Path.ChangeExtension(Path.GetFileName(source.Path), "webp"),
            Data = data,
            ContentType = _ddbWrapper.ThumbnailMimeType
        };
    }

    public async Task<StorageDataDto> GenerateTileDataExAsync(string orgSlug, string dsSlug, string path, int tz,
        int tx, int ty, bool retina, RasterVisualization visualization, CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        var v = visualization;

        return await CachedTileAsync(ty, () => _cache.GetAsync(MagicStrings.TileCacheSeed,
            ForDataset(orgSlug, dsSlug), source.Entry.Hash, tx, ty, tz, retina, v.CacheKey,
            new Func<Task<byte[]>>(() => _gate.RunAsync(NativeLane.Tiles, () => source.Ddb.GenerateTileEx(
                source.SourcePath, tz, tx, ty, retina, source.Entry.Hash, v.Preset, v.Bands, v.Formula,
                v.BandFilter, v.Colormap, v.Rescale).ToWebp(90), ct))));
    }

    // A tile is a webp named after its row; a tile outside the raster is a 404, not a server fault
    private static async Task<StorageDataDto> CachedTileAsync(int ty, Func<Task<byte[]>> tile)
    {
        try
        {
            return new StorageDataDto { Data = await tile(), ContentType = TileMediaType, Name = $"{ty}.webp" };
        }
        catch (InvalidOperationException ex) when (ex.InnerException?.Message.Contains("Out of bounds",
                                                        StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new NotFoundException("Tile out of bounds", ex);
        }
    }

    private async Task<StorageDataDto?> GenerateDatasetThumbnailDataAsync(string orgSlug, string dsSlug, int? size,
        bool recreate, CancellationToken ct)
    {
        var ddb = await _sources.OpenReadableAsync(orgSlug, dsSlug, ct);

        // The first candidate the dataset has
        var thumbnailPath = _options.Candidates.FirstOrDefault(ddb.EntryExists);
        if (thumbnailPath == null)
            return null;

        var pixels = size ?? _options.DefaultThumbnailSize;
        var category = ForDatasetThumbnail(orgSlug, dsSlug);

        if (recreate)
        {
            _logger.LogDebug("Removing cached dataset thumbnail for {OrgSlug}/{DsSlug}", orgSlug, dsSlug);
            await _cache.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed, category);
        }

        var localPath = ddb.GetLocalPath(thumbnailPath);
        var data = await _cache.GetAsync(MagicStrings.ThumbnailCacheSeed, category, thumbnailPath, pixels,
            new Func<Task<byte[]>>(() => RenderThumbnailAsync(localPath, pixels)));

        return new StorageDataDto { Name = "thumbnail.webp", Data = data, ContentType = _ddbWrapper.ThumbnailMimeType };
    }

    // The generator is gated itself (GatedThumbnailGenerator, heavy lane)
    private async Task<byte[]> RenderThumbnailAsync(string localPath, int size)
    {
        _logger.LogDebug("Cache miss - generating a thumbnail of '{LocalPath}'", localPath);
        using var stream = new MemoryStream();
        await _thumbnails.GenerateThumbnailAsync(localPath, size, stream);
        return stream.ToArray();
    }
}
