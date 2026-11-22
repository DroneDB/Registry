using System;
using System.IO;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.Reports;

/// <summary>A logo that passed the checks, ready to be stored: its (sanitised) bytes, the extension of its file and its media type.</summary>
public sealed record ReportLogo(byte[] Content, string Extension, string MediaType)
{
    /// <summary>The largest logo accepted, in bytes.</summary>
    public const int MaxBytes = 512 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    /// <summary>The media type a logo file with <paramref name="extension"/> is served as, or null when it is not a logo extension.</summary>
    public static string? MediaTypeOfExtension(string extension) => extension switch
    {
        "png" => "image/png",
        "jpg" => "image/jpeg",
        "webp" => "image/webp",
        "svg" => "image/svg+xml",
        _ => null
    };

    /// <summary>Refuses a logo of <paramref name="length"/> bytes that is over <see cref="MaxBytes"/> (before its bytes are read).</summary>
    /// <exception cref="BadRequestException">Too large.</exception>
    public static void EnsureSize(long length)
    {
        if (length > MaxBytes)
            throw new BadRequestException($"The logo can be at most {MaxBytes / 1024} KB");
    }

    /// <summary>
    /// Checks the bytes of an uploaded logo and returns what to store. The type is found from the content (the
    /// signature of a PNG, a JPEG or a WebP, the root of an SVG), never from the name or the type the client sent, and
    /// an SVG is replaced by its sanitised copy (<see cref="SvgSanitizer"/>).
    /// </summary>
    /// <exception cref="BadRequestException">Empty, over <see cref="MaxBytes"/>, or not a PNG, JPEG, WebP or SVG.</exception>
    public static ReportLogo Inspect(byte[] bytes)
    {
        if (bytes.Length == 0)
            throw new BadRequestException("The logo file is empty");

        EnsureSize(bytes.Length);

        if (bytes.AsSpan().StartsWith(PngSignature))
            return Of(bytes, "png");

        if (bytes.AsSpan().StartsWith(JpegSignature))
            return Of(bytes, "jpg");

        if (IsWebP(bytes))
            return Of(bytes, "webp");

        try
        {
            return Of(SvgSanitizer.Sanitize(bytes), "svg");
        }
        catch (InvalidDataException ex)
        {
            throw new BadRequestException($"The logo must be a PNG, JPEG, WebP or SVG image: {ex.Message}");
        }
    }

    private static ReportLogo Of(byte[] content, string extension) =>
        new(content, extension, MediaTypeOfExtension(extension)!);

    // RIFF <size> WEBP
    private static bool IsWebP(byte[] bytes) =>
        bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
}
