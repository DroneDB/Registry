using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities;
// Ref https://docs.microsoft.com/it-IT/aspnet/core/mvc/models/file-uploads?view=aspnetcore-3.1#upload-large-files-with-streaming

public static class MultipartRequestHelper
{
    // Content-Type: multipart/form-data; boundary="----WebKitFormBoundarymx2fSWqWSd0OxQqq"
    // The spec at https://tools.ietf.org/html/rfc2046#section-5.1 states that 70 characters is a reasonable limit.
    public static string GetBoundary(MediaTypeHeaderValue contentType, int lengthLimit)
    {
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;

        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidDataException("Missing content-type boundary.");
        }

        if (boundary.Length > lengthLimit)
        {
            throw new InvalidDataException(
                $"Multipart boundary length limit {lengthLimit} exceeded.");
        }

        return boundary;
    }

    public static bool IsMultipartContentType(string? contentType)
    {
        return !string.IsNullOrEmpty(contentType)
               && contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasFormDataContentDisposition(ContentDispositionHeaderValue contentDisposition)
    {
        // Content-Disposition: form-data; name="key";
        return contentDisposition != null
               && contentDisposition.DispositionType.Equals("form-data")
               && string.IsNullOrEmpty(contentDisposition.FileName.Value)
               && string.IsNullOrEmpty(contentDisposition.FileNameStar.Value);
    }

    public static bool HasFileContentDisposition(ContentDispositionHeaderValue contentDisposition)
    {
        // Content-Disposition: form-data; name="myfile1"; filename="Misc 002.jpg"
        return contentDisposition != null
               && contentDisposition.DispositionType.Equals("form-data")
               && (!string.IsNullOrEmpty(contentDisposition.FileName.Value)
                   || !string.IsNullOrEmpty(contentDisposition.FileNameStar.Value));
    }

    /// <summary>
    /// Reads a streamed <c>multipart/form-data</c> body with at most one file part. The file part is
    /// handed to <paramref name="streamFile"/> as soon as it arrives (it writes it to the dataset volume
    /// and returns the temp file); the form fields are collected whatever their order. On failure the
    /// temp file is deleted.
    /// </summary>
    /// <param name="request">The request (multipart content type checked by the caller).</param>
    /// <param name="boundaryLengthLimit">Maximum boundary length (<c>FormOptions.MultipartBoundaryLengthLimit</c>).</param>
    /// <param name="streamFile">Writes the file part body to a temp file and returns its path and size.</param>
    /// <param name="discard">Deletes a temp file written by <paramref name="streamFile"/> (on failure).</param>
    /// <returns>The form fields and the temp file (null when the body has no file part).</returns>
    /// <exception cref="BadRequestException">The body has more than one file part.</exception>
    public static async Task<StreamedMultipartForm> ReadSingleFileFormAsync(HttpRequest request,
        int boundaryLengthLimit, Func<Stream, Task<(string TempPath, long Bytes)>> streamFile, Action<string> discard)
    {
        var files = await ReadFilesFormAsync(request, boundaryLengthLimit, 1,
            part => streamFile(part.Body), file => discard(file.TempPath));

        var form = new StreamedMultipartForm();
        foreach (var (key, value) in files.Fields)
            form.Fields[key] = value;
        if (files.Files.Count > 0)
            (form.TempFile, form.Bytes) = files.Files[0].File;
        return form;
    }

    /// <summary>
    /// Reads a streamed <c>multipart/form-data</c> body with up to <paramref name="maxFiles"/> file
    /// parts. Each file part is handed to <paramref name="streamFile"/> as it arrives, with the
    /// <c>path</c> field sent just before it (null when there is none), so the destination can be
    /// checked before the first byte of the file is written. The form fields are collected whatever
    /// their order. On failure every file already written is handed to <paramref name="discard"/>.
    /// </summary>
    /// <exception cref="BadRequestException">The body has more than <paramref name="maxFiles"/> file parts.</exception>
    public static async Task<StreamedMultipartFiles<TFile>> ReadFilesFormAsync<TFile>(HttpRequest request,
        int boundaryLengthLimit, int maxFiles, Func<MultipartFilePart, Task<TFile>> streamFile,
        Action<TFile> discard)
    {
        var boundary = GetBoundary(MediaTypeHeaderValue.Parse(request.ContentType), boundaryLengthLimit);
        var reader = new MultipartReader(boundary, request.Body);
        var form = new StreamedMultipartFiles<TFile>();
        string? pendingPath = null;

        try
        {
            for (var section = await reader.ReadNextSectionAsync(); section != null;
                 section = await reader.ReadNextSectionAsync())
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                    continue;

                if (HasFileContentDisposition(contentDisposition))
                {
                    if (form.Files.Count >= maxFiles)
                        throw new BadRequestException(maxFiles == 1
                            ? "Only a single file part is allowed per upload"
                            : $"At most {maxFiles} file parts are allowed per upload");

                    var file = await streamFile(new MultipartFilePart(section.Body, pendingPath, form.Fields));
                    form.Files.Add((pendingPath, file));
                    pendingPath = null;
                }
                else if (HasFormDataContentDisposition(contentDisposition))
                {
                    var key = HeaderUtilities.RemoveQuotes(contentDisposition.Name).Value;
                    using var sr = new StreamReader(section.Body, Encoding.UTF8);
                    var value = await sr.ReadToEndAsync();
                    // A part with no name has a null key, which the dictionary refuses (ArgumentNullException)
                    form.Fields[key!] = value;
                    if (string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
                        pendingPath = value;
                }
            }
        }
        catch
        {
            foreach (var (_, file) in form.Files)
                discard(file);
            throw;
        }

        return form;
    }
}

/// <summary>A file part of a streamed multipart body, with the <c>path</c> field sent before it.</summary>
/// <param name="Body">The file content (forward-only).</param>
/// <param name="Path">The <c>path</c> field that precedes the part, or null.</param>
/// <param name="Fields">Every field sent before the part.</param>
public sealed record MultipartFilePart(Stream Body, string? Path, IReadOnlyDictionary<string, string> Fields);

/// <summary>The form fields and the file parts (already on disk) of a streamed multipart upload.</summary>
public sealed class StreamedMultipartFiles<TFile>
{
    /// <summary>Form fields by name (case-insensitive; the last value of a repeated field).</summary>
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file parts in order, with the <c>path</c> field sent just before each one (or null).</summary>
    public List<(string? Path, TFile File)> Files { get; } = [];

    /// <summary>The value of a form field, or null.</summary>
    public string? GetField(string name) => Fields.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// The form fields and the single file part (already on disk) of a streamed multipart upload.
/// </summary>
public sealed class StreamedMultipartForm
{
    /// <summary>Form fields by name (case-insensitive).</summary>
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The temp file holding the file part, or null when there was none.</summary>
    public string? TempFile { get; set; }

    /// <summary>Size of <see cref="TempFile"/>.</summary>
    public long Bytes { get; set; }

    /// <summary>The value of a form field, or null.</summary>
    public string? GetField(string name) => Fields.TryGetValue(name, out var value) ? value : null;
}
