#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Web.Exceptions;
using Registry.Web.Utilities;
using SkiaSharp;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The image format of a map tile, chosen by the extension of the tile URL (<c>.png</c>, <c>.webp</c>,
/// <c>.jpg</c>): the one table of its media type, the name libddb takes (<c>DDBMemoryTileFmt</c>) and the
/// encoder for the tiles libddb renders as PNG only (those with visualization parameters). The bytes
/// served always match the extension asked for and the <c>Content-Type</c> sent.
/// </summary>
public sealed record TileFormat
{
    /// <summary>The extensions the tile routes accept, as a route constraint alternation.</summary>
    public const string RouteExtensions = "png|webp|jpg";

    private const int EncodeQuality = 90;

    public static readonly TileFormat Png = new("png", "image/png", "png", SKEncodedImageFormat.Png);
    public static readonly TileFormat Webp = new("webp", "image/webp", "webp", SKEncodedImageFormat.Webp);
    public static readonly TileFormat Jpeg = new("jpg", "image/jpeg", "jpeg", SKEncodedImageFormat.Jpeg);

    private static readonly IReadOnlyList<TileFormat> All = [Png, Webp, Jpeg];

    private readonly SKEncodedImageFormat _encoding;

    private TileFormat(string extension, string mediaType, string ddbName, SKEncodedImageFormat encoding)
    {
        Extension = extension;
        MediaType = mediaType;
        DdbName = ddbName;
        _encoding = encoding;
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

    /// <summary>
    /// A tile libddb rendered as a PNG, in this format: the PNG itself for <see cref="Png"/>, else decoded and
    /// encoded again.
    /// </summary>
    public byte[] FromPng(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        return _encoding == SKEncodedImageFormat.Png ? png : png.Encode(_encoding, EncodeQuality);
    }
}

/// <summary>Reading a tile libddb rendered as a PNG in a <see cref="TileFormat"/>.</summary>
public static class TileFormatExtensions
{
    /// <summary>The PNG tile in <paramref name="format"/> (see <see cref="TileFormat.FromPng"/>).</summary>
    public static byte[] InFormat(this byte[] png, TileFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format.FromPng(png);
    }
}
