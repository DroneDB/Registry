using System;
using System.IO;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.OrganizationAssets;

/// <summary>
/// An image an organization owns (a report-template logo, the logo of the organization) that passed the checks, ready
/// to be stored: its (sanitised) bytes, the extension of its type and its media type.
/// </summary>
public sealed record OrganizationImage(byte[] Content, string Extension, string MediaType)
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    /// <summary>The leading bytes that tell a PNG, a JPEG and a WebP from anything else (the RIFF header is 12 bytes).</summary>
    public const int SniffBytes = 12;

    /// <summary>The media type an image file with <paramref name="extension"/> is served as, or null when it is not an image extension.</summary>
    public static string? MediaTypeOfExtension(string extension) => extension switch
    {
        "png" => "image/png",
        "jpg" => "image/jpeg",
        "webp" => "image/webp",
        "svg" => "image/svg+xml",
        _ => null
    };

    /// <summary>
    /// The media type of a stored image from its leading bytes (at least <see cref="SniffBytes"/> of them, or the
    /// whole file when it is shorter): the signature of a PNG, a JPEG or a WebP, and otherwise the SVG that the
    /// upload sanitised. For a file whose name has no extension to say.
    /// </summary>
    public static string MediaTypeOfContent(ReadOnlySpan<byte> head) =>
        MediaTypeOfExtension(ExtensionOfSignature(head) ?? "svg")!;

    /// <summary>Refuses an image of <paramref name="length"/> bytes that is over <paramref name="maxBytes"/> (before its bytes are read).</summary>
    /// <exception cref="BadRequestException">Too large.</exception>
    public static void EnsureSize(long length, int maxBytes)
    {
        if (length > maxBytes)
            throw new BadRequestException($"The logo can be at most {maxBytes / 1024} KB");
    }

    /// <summary>The refusal of a file that is not one of the types.</summary>
    public const string NotAnImageMessage = "The file is not a PNG, JPEG, WebP or SVG image";

    /// <summary>
    /// Checks the bytes of an uploaded image and returns what to store. The type is found from the content (the
    /// signature of a PNG, a JPEG or a WebP, the root of an SVG), never from the name or the type the client sent, and
    /// an SVG is replaced by its sanitised copy (<see cref="SvgSanitizer"/>).
    /// </summary>
    /// <param name="bytes">What the client sent.</param>
    /// <param name="maxBytes">The largest image the feature accepts.</param>
    /// <exception cref="BadRequestException">Empty, over <paramref name="maxBytes"/>, or not a PNG, JPEG, WebP or SVG.</exception>
    public static OrganizationImage Inspect(byte[] bytes, int maxBytes)
    {
        if (bytes.Length == 0)
            throw new BadRequestException("The logo file is empty");

        EnsureSize(bytes.Length, maxBytes);

        if (ExtensionOfSignature(bytes) is { } extension)
            return Of(bytes, extension);

        try
        {
            return Of(SvgSanitizer.Sanitize(bytes), "svg");
        }
        catch (NotSvgDocumentException)
        {
            // Not one of the four types: say which types are taken, not why the bytes are no SVG (they were only
            // tried as one because they are no PNG, JPEG or WebP)
            throw new BadRequestException(NotAnImageMessage);
        }
        catch (InvalidDataException ex)
        {
            throw new BadRequestException($"This SVG cannot be used: {ex.Message}");
        }
    }

    private static OrganizationImage Of(byte[] content, string extension) =>
        new(content, extension, MediaTypeOfExtension(extension)!);

    // The one place the signatures are: the check of an upload and the type a stored file is served as
    private static string? ExtensionOfSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PngSignature))
            return "png";

        if (bytes.StartsWith(JpegSignature))
            return "jpg";

        return IsWebP(bytes) ? "webp" : null;
    }

    // RIFF <size> WEBP
    private static bool IsWebP(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8);
}
