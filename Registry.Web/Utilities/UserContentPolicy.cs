using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;

namespace Registry.Web.Utilities;

/// <summary>
/// How the bytes a user put in a dataset (a raw file, or a file a build made of one: the members of a
/// <c>.3tz</c> are the user's own files) are sent. They are served from the origin of the Hub, where the
/// session token of the signed-in user can be read, so a response a browser would render as a document
/// (<c>.html</c>, <c>.svg</c>, <c>.xml</c>, <c>.js</c>, a file whose name only says so) would run the user's
/// script with that token. The type of a file is therefore never taken from what its name or its builder says:
/// only the types in an allow-list of what the Hub reads (tiles, models, images, and for raw files the
/// documents and media it shows in place) keep their type and may be shown in place; every other file is
/// <c>application/octet-stream</c> and offered as an attachment, which no browser renders.
/// <para>
/// Every response also carries <c>Content-Security-Policy: sandbox</c>, which gives whatever a browser still
/// renders an opaque origin without script, except for the types whose viewer does not work in a sandbox
/// (the PDF viewer of Chrome) or has nothing to run (audio, video).
/// </para>
/// </summary>
public static class UserContentPolicy
{
    /// <summary>The type of everything that is not in the allow-list.</summary>
    public const string OctetStream = "application/octet-stream";

    /// <summary>The value of the <c>Content-Security-Policy</c> header that confines a response.</summary>
    public const string Sandbox = "sandbox";

    // The files of a build the Hub reads (3D Tiles and their payloads, images and textures, GeoTIFFs); read by
    // fetch or as image sources, never shown as a document
    private static readonly KeyValuePair<string, string>[] BuildTypes =
    [
        new("json", "application/json"),
        new("gltf", "model/gltf+json"),
        new("glb", "model/gltf-binary"),
        new("b3dm", OctetStream),
        new("i3dm", OctetStream),
        new("pnts", OctetStream),
        new("cmpt", OctetStream),
        new("bin", OctetStream),
        new("subtree", OctetStream),
        new("basis", OctetStream),
        new("jpg", "image/jpeg"),
        new("jpeg", "image/jpeg"),
        new("png", "image/png"),
        new("webp", "image/webp"),
        new("ktx2", "image/ktx2"),
        new("avif", "image/avif"),
        new("gif", "image/gif"),
        new("tif", "image/tiff"),
        new("tiff", "image/tiff"),
    ];

    // What the Hub shows in place in a tab, a lightbox or a viewer: images, PDF, audio and video, text. Every
    // extension libddb makes an Image or a Video entry of is here, because the lightbox plays a video from the
    // download URL and opens a photo or a video in a tab
    private static readonly KeyValuePair<string, string>[] EntryOnlyTypes =
    [
        new("bmp", "image/bmp"),
        new("dng", "image/x-adobe-dng"),
        new("jp2", "image/jp2"),
        new("j2k", "image/j2c"),
        new("pdf", "application/pdf"),
        new("mp4", "video/mp4"),
        new("m4v", "video/mp4"),
        new("webm", "video/webm"),
        new("mov", "video/quicktime"),
        new("mkv", "video/x-matroska"),
        new("avi", "video/x-msvideo"),
        new("ogv", "video/ogg"),
        new("mp3", "audio/mpeg"),
        new("wav", "audio/wav"),
        new("ogg", "audio/ogg"),
        new("m4a", "audio/mp4"),
        new("flac", "audio/flac"),
        new("txt", "text/plain"),
        new("log", "text/plain"),
        new("csv", "text/plain"),
        new("md", "text/plain"),
        new("geojson", "application/json"),
    ];

    private static readonly FrozenDictionary<string, string> BuildFileTypes = ByExtension(BuildTypes);

    private static readonly FrozenDictionary<string, string> EntryTypes =
        ByExtension([.. BuildTypes, .. EntryOnlyTypes]);

    /// <summary>
    /// The media type and disposition of a file a build made. <paramref name="declaredMediaType"/>, the one
    /// libddb gives for an artifact, is kept (with its parameters) when it is a type the allow-list holds.
    /// </summary>
    public static ServedContent ForBuildFile(string path, string? declaredMediaType = null) =>
        Resolve(BuildFileTypes, path, declaredMediaType, alwaysSandboxed: true);

    /// <summary>The media type and disposition of a raw file of a dataset.</summary>
    public static ServedContent ForEntry(string path) =>
        Resolve(EntryTypes, path, declaredMediaType: null, alwaysSandboxed: false);

    /// <summary>
    /// The <c>Content-Security-Policy</c> of a response of <paramref name="mediaType"/>, or null for none: the
    /// types whose viewer is not a document (so nothing of the user's can run) or does not work under a sandbox.
    /// </summary>
    public static string? ContentSecurityPolicyFor(string mediaType) =>
        IsViewerType(EssenceOf(mediaType)) ? null : Sandbox;

    private static ServedContent Resolve(FrozenDictionary<string, string> allowed, string path,
        string? declaredMediaType, bool alwaysSandboxed)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        if (!allowed.TryGetValue(extension, out var mediaType))
            return new ServedContent(OctetStream, Inline: false, Sandbox);

        // The declared type refines the one of the extension (image/tiff with its profile), it never changes the
        // kind of the file
        if (declaredMediaType is not null && EssenceOf(declaredMediaType) == EssenceOf(mediaType))
            mediaType = declaredMediaType;

        return new ServedContent(mediaType, Inline: true,
            alwaysSandboxed ? Sandbox : ContentSecurityPolicyFor(mediaType));
    }

    private static string EssenceOf(string mediaType)
    {
        var parameters = mediaType.IndexOf(';', StringComparison.Ordinal);
        return (parameters < 0 ? mediaType : mediaType[..parameters]).Trim().ToLowerInvariant();
    }

    private static bool IsViewerType(string essence) =>
        essence == "application/pdf" || essence.StartsWith("video/", StringComparison.Ordinal) ||
        essence.StartsWith("audio/", StringComparison.Ordinal);

    private static FrozenDictionary<string, string> ByExtension(IEnumerable<KeyValuePair<string, string>> types) =>
        types.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// How a file is sent: its <paramref name="MediaType"/>, whether a browser may show it in place
/// (<paramref name="Inline"/>; otherwise it is an attachment) and the <paramref name="ContentSecurityPolicy"/> of
/// the response, or null for none.
/// </summary>
public readonly record struct ServedContent(string MediaType, bool Inline, string? ContentSecurityPolicy);
