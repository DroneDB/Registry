#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports;

/// <summary>What a chunked upload will produce, fixed by its first chunk.</summary>
/// <param name="Path">Destination path in the dataset.</param>
/// <param name="TotalSize">Size of the whole file.</param>
/// <param name="TotalChunks">Number of chunks.</param>
/// <param name="ChunkSize">Size of every chunk but the last.</param>
/// <param name="UserId">Who started it.</param>
public sealed record ChunkManifest(string Path, long TotalSize, int TotalChunks, long ChunkSize, string? UserId)
{
    /// <summary>Byte offset of chunk <paramref name="index"/>.</summary>
    public long OffsetOf(int index) => index * ChunkSize;

    /// <summary>Size of chunk <paramref name="index"/>.</summary>
    public long LengthOf(int index) => index == TotalChunks - 1 ? TotalSize - OffsetOf(index) : ChunkSize;
}

/// <summary>Which chunks of an upload are stored.</summary>
public sealed record ChunkStatus(ChunkManifest Manifest, IReadOnlyList<int> Received)
{
    public IReadOnlyList<int> Missing =>
        Enumerable.Range(0, Manifest.TotalChunks).Except(Received).ToArray();

    public bool Complete => Received.Count == Manifest.TotalChunks;
}

/// <summary>
/// Durable storage of resumable chunked uploads (UPL-01 E.5). Everything that defines an upload is
/// on disk (manifest, chunk markers, one preallocated data file the chunks are written into at
/// their offset), so chunks can arrive in any request, in any order, on any instance sharing the
/// storage, and across a restart. The SHA-256 is computed as the chunks arrive in order, so
/// taking the finished file needs no extra pass over it.
/// </summary>
public interface IChunkStore
{
    /// <summary>
    /// Starts an upload, or resumes it when <paramref name="fileId"/> already exists with the same
    /// manifest.
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.ConflictException">The id exists with another manifest.</exception>
    ChunkStatus Open(string root, string fileId, ChunkManifest manifest);

    /// <summary>The state of an upload, or null when unknown.</summary>
    ChunkStatus? GetStatus(string root, string fileId);

    /// <summary>
    /// Stores chunk <paramref name="index"/> from <paramref name="body"/>. A chunk already stored
    /// is accepted again without being rewritten.
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.BadRequestException">The body is not the chunk's size.</exception>
    Task<ChunkStatus> WriteAsync(string root, string fileId, int index, Stream body, CancellationToken ct = default);

    /// <summary>
    /// Moves the finished file to <paramref name="destination"/> and forgets the upload.
    /// </summary>
    /// <returns>The file's hash hint (its SHA-256, size and write time).</returns>
    /// <exception cref="Registry.Web.Exceptions.ConflictException">Chunks are missing.</exception>
    Task<FileHashHint> TakeAsync(string root, string fileId, string destination);
}
