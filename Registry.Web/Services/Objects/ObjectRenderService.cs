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
using Registry.Web.Services.Ports;
using static Registry.Web.Services.CacheCategories;

namespace Registry.Web.Services.Objects;

/// <summary>
/// Default <see cref="IObjectRenderService"/>: renders are cached per entry hash and version of the rendered file
/// (<see cref="RenderCacheKeys"/>; the dataset thumbnail per candidate path) and every native render runs through <see cref="INativeWorkGate"/> (tiles on the tiles
/// lane, thumbnails on the heavy lane, through the gated thumbnail generator). A render runs with the cache
/// flight's token: it stops when every request waiting for it has gone.
/// </summary>
public sealed class ObjectRenderService : IObjectRenderService
{
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

    public Task<ReadableObject> ResolveThumbnailObjectAsync(string orgSlug, string dsSlug, string? path,
        CancellationToken ct = default) =>
        _sources.ResolveThumbnailAsync(orgSlug, dsSlug, path, ct);

    public async Task<StorageDataDto?> GenerateThumbnailDataAsync(string orgSlug, string dsSlug, string? path,
        int? size, bool recreate = false, CancellationToken ct = default)
    {
        _logger.LogInformation("Thumbnail of '{OrgSlug}/{DsSlug}', path: '{Path}', size: {Size}, recreate: {Recreate}",
            orgSlug, dsSlug, path, size, recreate);

        // Dataset thumbnail mode: path is null or empty
        if (string.IsNullOrWhiteSpace(path))
            return await GenerateDatasetThumbnailDataAsync(orgSlug, dsSlug, size, recreate, ct);

        var obj = await _sources.ResolveThumbnailAsync(orgSlug, dsSlug, path, ct);

        if (recreate)
        {
            var pixels = ClampSize(size);
            _logger.LogDebug("Removing cached thumbnail for {OrgSlug}/{DsSlug}, hash: {Hash}, size: {Size}",
                orgSlug, dsSlug, obj.Hash, pixels);
            // The key of the read below, otherwise nothing is removed
            await _cache.RemoveAsync(MagicStrings.ThumbnailCacheSeed, ForDataset(orgSlug, dsSlug), ThumbnailKey(obj, pixels));
        }

        return await GenerateThumbnailDataAsync(obj, size, ct);
    }

    public async Task<StorageDataDto> GenerateThumbnailDataAsync(ReadableObject obj, int? size,
        CancellationToken ct = default)
    {
        var localPath = obj.Ddb.GetLocalPath(obj.SourcePath);
        var pixels = ClampSize(size);

        var data = await _cache.GetAsync(MagicStrings.ThumbnailCacheSeed, ForDataset(obj.OrgSlug, obj.DsSlug), ct,
            ThumbnailKey(obj, pixels).WithRender(token => RenderThumbnailAsync(localPath, pixels, token)));

        var extension = MimeUtility.GetExtensions(_ddbWrapper.ThumbnailMimeType)?.FirstOrDefault() ?? "webp";

        return new StorageDataDto
        {
            Name = Path.ChangeExtension(Path.GetFileName(obj.Path), extension),
            Data = data,
            ContentType = _ddbWrapper.ThumbnailMimeType
        };
    }

    public Task<StorageDataDto> GenerateTileDataAsync(ReadableObject obj, int tz, int tx, int ty, bool retina,
        TileFormat format, CancellationToken ct = default) =>
        CachedTileAsync(ty, format, () => PlainTileCache.GetAsync(_cache, _gate, obj.OrgSlug, obj.DsSlug, obj.Ddb,
            obj.SourcePath, obj.Hash, obj.SourceVersion, tz, tx, ty, retina, format, ct));

    public async Task<StorageDataDto> GenerateThumbnailDataExAsync(string orgSlug, string dsSlug, string path,
        int? size, RasterVisualization visualization, CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        var pixels = ClampSize(size);
        var v = Checked(visualization);

        var data = await _cache.GetAsync(MagicStrings.ThumbnailCacheSeed, ForDataset(orgSlug, dsSlug), ct,
            RenderCacheKeys.ThumbnailEx(source.Entry.FileHash(), source.Version, pixels, v.CacheKey).WithRender(token =>
                _gate.RunAsync(NativeLane.Heavy, () => v.Options is null
                    ? source.Ddb.GenerateThumbnailEx(source.SourcePath, pixels, v.Preset, v.Bands, v.Formula,
                        v.BandFilter, v.Colormap, v.Rescale)
                    : source.Ddb.GenerateThumbnailEx(source.SourcePath, pixels, v.Preset, v.Bands, v.Formula,
                        v.BandFilter, v.Colormap, v.Rescale, v.Options), token)));

        return new StorageDataDto
        {
            Name = Path.ChangeExtension(Path.GetFileName(source.Path), "webp"),
            Data = data,
            ContentType = _ddbWrapper.ThumbnailMimeType
        };
    }

    public async Task<StorageDataDto> GenerateTileDataExAsync(string orgSlug, string dsSlug, string path, int tz,
        int tx, int ty, bool retina, RasterVisualization visualization, TileFormat format,
        CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        var v = Checked(visualization);

        // libddb encodes the visualization tile once in the format asked for, so its bytes are served (and cached
        // under that format) as they come, without a decode and a second encode here
        return await CachedTileAsync(ty, format, () => _cache.GetAsync(MagicStrings.TileCacheSeed,
            ForDataset(orgSlug, dsSlug), ct,
            RenderCacheKeys.TileEx(source.Entry.FileHash(), source.Version, tx, ty, tz, retina, v.CacheKey,
                format.MediaType).WithRender(token => _gate.RunAsync(NativeLane.Tiles,
                () => source.Ddb.GenerateTileEx(source.SourcePath, tz, tx, ty, retina, source.Entry.FileHash(),
                    v.Preset, v.Bands, v.Formula, v.BandFilter, v.Colormap, v.Rescale, v.Options, format.DdbName),
                token))));
    }

    // The size of a thumbnail asked for (the default when none): never above what the Hub asks for at most, so that a
    // request cannot make the server render, cache and send an image of any size. Every thumbnail path takes its size
    // here, so a size above the bound shares the entry of the bound.
    private int ClampSize(int? size) => ThumbnailOptions.Bounded(size ?? _options.DefaultThumbnailSize);

    // The one key of the plain thumbnail of an entry, for the read and for the removal that precedes a recreate
    private static object[] ThumbnailKey(ReadableObject obj, int pixels) =>
        RenderCacheKeys.Thumbnail(obj.Hash, obj.SourceVersion, pixels);

    // The NoData value is a finite number and the unit of the rescale one libddb knows (400s), before any native work
    private static RasterVisualization Checked(RasterVisualization visualization)
    {
        RasterOptionRules.RequireNodata(visualization.Nodata);
        return visualization with
        {
            RescaleUnit = RasterOptionRules.RescaleUnit(visualization.RescaleUnit)
        };
    }

    // A tile is an image in the format asked for, named after its row; a tile outside the raster is a 404, not a
    // server fault
    private static async Task<StorageDataDto> CachedTileAsync(int ty, TileFormat format, Func<Task<byte[]>> tile)
    {
        try
        {
            return new StorageDataDto { Data = await tile(), ContentType = format.MediaType, Name = $"{ty}.{format.Extension}" };
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

        // The first candidate the dataset has (a folder of that name is not a cover)
        var cover = _options.Candidates.Select(ddb.GetEntry).FirstOrDefault(entry => entry is { Hash: not null });
        if (cover == null)
            return null;

        var pixels = ClampSize(size);
        var category = ForDatasetThumbnail(orgSlug, dsSlug);
        // The path and the hash of the cover are in the key: a new cover, under the name of the old one too, is read
        // from an entry of its own. The in-memory cache cannot remove a category, so a key without the hash served
        // the render of the old cover until it expired.
        var key = RenderCacheKeys.DatasetThumbnail(cover.Path, cover.FileHash(), pixels);

        if (recreate)
        {
            _logger.LogDebug("Removing cached dataset thumbnail for {OrgSlug}/{DsSlug}", orgSlug, dsSlug);
            // The key of the read below, otherwise nothing is removed
            await _cache.RemoveAsync(MagicStrings.ThumbnailCacheSeed, category, key);
        }

        var localPath = ddb.GetLocalPath(cover.Path);
        var data = await _cache.GetAsync(MagicStrings.ThumbnailCacheSeed, category, ct,
            key.WithRender(token => RenderThumbnailAsync(localPath, pixels, token)));

        return new StorageDataDto { Name = "thumbnail.webp", Data = data, ContentType = _ddbWrapper.ThumbnailMimeType };
    }

    // The generator is gated itself (GatedThumbnailGenerator, heavy lane); the token is the cache flight's
    private async Task<byte[]> RenderThumbnailAsync(string localPath, int size, CancellationToken ct)
    {
        _logger.LogDebug("Cache miss - generating a thumbnail of '{LocalPath}'", localPath);
        using var stream = new MemoryStream();
        await _thumbnails.GenerateThumbnailAsync(localPath, size, stream, ct);
        return stream.ToArray();
    }
}
