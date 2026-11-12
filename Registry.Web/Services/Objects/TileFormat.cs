using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The image format of a map tile, chosen by the extension of the tile URL (<c>.png</c>, <c>.webp</c>,
/// <c>.jpg</c>): the one table of its media type and the name libddb takes. libddb encodes every tile, plain
/// (<c>DDBMemoryTileFmt</c>) or with visualization parameters (<c>DDBMemoryTileEx3</c>), once in that format,
/// so the bytes served match the extension asked for and the <c>Content-Type</c> sent without a second encode.
/// </summary>
public sealed record TileFormat
{
    /// <summary>The extensions the tile routes accept, as a route constraint alternation.</summary>
    public const string RouteExtensions = "png|webp|jpg";

    public static readonly TileFormat Png = new("png", "image/png", "png");
    public static readonly TileFormat Webp = new("webp", "image/webp", "webp");
    public static readonly TileFormat Jpeg = new("jpg", "image/jpeg", "jpeg");

    private static readonly IReadOnlyList<TileFormat> All = [Png, Webp, Jpeg];

    private TileFormat(string extension, string mediaType, string ddbName)
    {
        Extension = extension;
        MediaType = mediaType;
        DdbName = ddbName;
    }

    /// <summary>The extension, without the dot: also the file name of a served tile.</summary>
    public string Extension { get; }

    /// <summary>The <c>Content-Type</c> of the bytes.</summary>
    public string MediaType { get; }

    /// <summary>The format name libddb takes (<c>png</c>, <c>webp</c>, <c>jpeg</c>).</summary>
    public string DdbName { get; }

    /// <summary>The format of a tile URL's extension (case-insensitive, with or without the dot).</summary>
    /// <exception cref="BadRequestException">An extension the tile routes do not serve.</exception>
    public static TileFormat FromExtension(string? extension) =>
        All.FirstOrDefault(f => string.Equals(f.Extension, extension?.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
        ?? throw new BadRequestException($"Unsupported tile format '{extension}' (use {string.Join(", ", All.Select(f => f.Extension))})");
}
