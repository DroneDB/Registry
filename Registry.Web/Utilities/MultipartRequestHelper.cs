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

    public static bool IsMultipartContentType(string contentType)
    {
        return !string.IsNullOrEmpty(contentType)
               && contentType.IndexOf("multipart/", StringComparison.OrdinalIgnoreCase) >= 0;
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
    /// Best-effort deletion of a streamed temporary upload file, used to clean up when a
    /// request is rejected before the temp file is committed (e.g. multiple file parts).
    /// </summary>
    /// <param name="path">The temp file path to delete; ignored when null or empty.</param>
    public static void TryDeleteTempFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* ignore */
        }
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
    /// <returns>The form fields and the temp file (null when the body has no file part).</returns>
    /// <exception cref="BadRequestException">The body has more than one file part.</exception>
    public static async Task<StreamedMultipartForm> ReadSingleFileFormAsync(HttpRequest request,
        int boundaryLengthLimit, Func<Stream, Task<(string TempPath, long Bytes)>> streamFile)
    {
        var boundary = GetBoundary(MediaTypeHeaderValue.Parse(request.ContentType), boundaryLengthLimit);
        var reader = new MultipartReader(boundary, request.Body);
        var form = new StreamedMultipartForm();

        try
        {
            for (var section = await reader.ReadNextSectionAsync(); section != null;
                 section = await reader.ReadNextSectionAsync())
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                    continue;

                if (HasFileContentDisposition(contentDisposition))
                {
                    if (form.TempFile != null)
                        throw new BadRequestException("Only a single file part is allowed per upload");

                    (form.TempFile, form.Bytes) = await streamFile(section.Body);
                }
                else if (HasFormDataContentDisposition(contentDisposition))
                {
                    var key = HeaderUtilities.RemoveQuotes(contentDisposition.Name).Value;
                    using var sr = new StreamReader(section.Body, Encoding.UTF8);
                    form.Fields[key] = await sr.ReadToEndAsync();
                }
            }
        }
        catch
        {
            TryDeleteTempFile(form.TempFile);
            throw;
        }

        return form;
    }
}

/// <summary>
/// The form fields and the single file part (already on disk) of a streamed multipart upload.
/// </summary>
public sealed class StreamedMultipartForm
{
    /// <summary>Form fields by name (case-insensitive).</summary>
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The temp file holding the file part, or null when there was none.</summary>
    public string TempFile { get; set; }

    /// <summary>Size of <see cref="TempFile"/>.</summary>
    public long Bytes { get; set; }

    /// <summary>The value of a form field, or null.</summary>
    public string GetField(string name) => Fields.TryGetValue(name, out var value) ? value : null;
}
