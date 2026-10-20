#nullable enable
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>A chunk of a resumable upload, as sent by the client.</summary>
/// <param name="FileId">Client-generated id of the upload (8 to 64 letters, digits, '-' or '_').</param>
/// <param name="ChunkIndex">0-based index of the chunk.</param>
/// <param name="TotalChunks">Number of chunks.</param>
/// <param name="TotalFileSize">Size of the whole file.</param>
/// <param name="ChunkSize">Size of every chunk but the last.</param>
/// <param name="Path">Destination path in the dataset.</param>
public sealed record ChunkRequest(string FileId, int ChunkIndex, int TotalChunks, long TotalFileSize, long ChunkSize,
    string Path);

/// <summary>
/// Resumable uploads of large files into a dataset (UPL-01 E.5): the client sends the file in
/// chunks, asks which chunks are stored after any failure, sends only those, then commits once.
/// The commit is idempotent (keyed by the file id) and needs no pass over the file.
/// </summary>
public interface IChunkedUploadManager
{
    /// <summary>Stores a chunk; the first chunk of an upload checks access, path and quota.</summary>
    Task<ChunkStatusDto> WriteChunkAsync(string orgSlug, string dsSlug, ChunkRequest chunk, Stream body,
        CancellationToken ct = default);

    /// <summary>The state of an upload.</summary>
    /// <exception cref="Registry.Web.Exceptions.NotFoundException">Unknown upload.</exception>
    Task<ChunkStatusDto> GetStatusAsync(string orgSlug, string dsSlug, string fileId);

    /// <summary>Adds the finished file to the dataset; again for a committed upload, returns its entry.</summary>
    /// <exception cref="Registry.Web.Exceptions.ConflictException">Chunks are missing.</exception>
    Task<EntryDto> CommitAsync(string orgSlug, string dsSlug, string fileId);
}
