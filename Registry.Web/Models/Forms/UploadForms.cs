using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Registry.Web.Models.Forms;

// The multipart fields of the streamed uploads, for the API reference only (StreamedRequestBodyAttribute):
// the actions read the body themselves, so the fields are listed in the order a client sends them.

/// <summary>The fields of <c>POST …/obj</c>: one file, a bundle of files, or a folder.</summary>
public sealed class ObjectUploadForm
{
    /// <summary>
    /// The dataset path of the next file part (or of the folder to create, without a file part). Send it before
    /// its file; repeat path, overwrite and file for each file of a bundle.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>true: replace the file whose name exists at path. Without it that file is refused with 409. Send it before its file part.</summary>
    public bool? Overwrite { get; init; }

    /// <summary>The file. Without a file part, a folder is created at path.</summary>
    public IFormFile? File { get; init; }
}

/// <summary>The fields of <c>POST …/obj/chunk</c>: one chunk of a resumable upload.</summary>
public sealed class DatasetChunkForm
{
    /// <summary>The id the client gives the upload: the same for every chunk of the file.</summary>
    [Required] public string? FileId { get; init; }

    /// <summary>The index of this chunk, from 0.</summary>
    [Required] public int? ChunkIndex { get; init; }

    /// <summary>The number of chunks of the file.</summary>
    [Required] public int? TotalChunks { get; init; }

    /// <summary>The size of the whole file in bytes.</summary>
    [Required] public long? TotalFileSize { get; init; }

    /// <summary>The size of every chunk but the last, in bytes.</summary>
    [Required] public long? ChunkSize { get; init; }

    /// <summary>The dataset path of the file.</summary>
    [Required] public string? Path { get; init; }

    /// <summary>true: replace the file whose name exists at path. The first chunk fixes it.</summary>
    public bool? Overwrite { get; init; }

    /// <summary>The name of the file on the client's disk. The first chunk fixes it; a file selected again after a reload is matched to its open upload by it.</summary>
    public string? FileName { get; init; }

    /// <summary>The file's last-modified time on the client, milliseconds since 1970. The first chunk fixes it.</summary>
    public long? LastModified { get; init; }

    /// <summary>The bytes of the chunk, after the other fields.</summary>
    [Required] public IFormFile? Chunk { get; init; }
}

/// <summary>The optional fields of <c>POST …/obj/chunk/{fileId}/commit</c>.</summary>
public sealed class ChunkCommitForm
{
    /// <summary>true: replace the file whose name exists, after a commit refused with 409.</summary>
    public bool? Overwrite { get; init; }
}

/// <summary>The fields of <c>POST /share/upload/{token}</c>.</summary>
public sealed class ShareUploadForm
{
    /// <summary>The dataset path of the file.</summary>
    [Required] public string? Path { get; init; }

    /// <summary>The file.</summary>
    [Required] public IFormFile? File { get; init; }
}

/// <summary>The fields of <c>POST /share/upload-chunk/{token}</c>: one chunk of a large file.</summary>
public sealed class ShareChunkForm
{
    /// <summary>The id the client gives the upload: the same for every chunk of the file.</summary>
    [Required] public string? FileId { get; init; }

    /// <summary>The index of this chunk, from 0.</summary>
    [Required] public int? ChunkIndex { get; init; }

    /// <summary>The number of chunks of the file.</summary>
    [Required] public int? TotalChunks { get; init; }

    /// <summary>The size of the whole file in bytes.</summary>
    [Required] public long? TotalFileSize { get; init; }

    /// <summary>The size of every chunk but the last, in bytes (required when the file has more than one chunk).</summary>
    public long? ChunkSize { get; init; }

    /// <summary>The dataset path of the file.</summary>
    [Required] public string? Path { get; init; }

    /// <summary>The name of the file.</summary>
    public string? FileName { get; init; }

    /// <summary>The bytes of the chunk, after the other fields.</summary>
    [Required] public IFormFile? Chunk { get; init; }
}
