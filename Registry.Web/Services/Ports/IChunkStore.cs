#nullable enable
using System.Collections.Generic;
using System.Linq;

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
