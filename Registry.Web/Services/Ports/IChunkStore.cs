using System.Collections.Generic;
using System.Linq;

namespace Registry.Web.Services.Ports;

/// <summary>What a chunked upload will produce, fixed by its first chunk.</summary>
/// <param name="Path">Destination path in the dataset.</param>
/// <param name="TotalSize">Size of the whole file.</param>
/// <param name="TotalChunks">Number of chunks.</param>
/// <param name="ChunkSize">Size of every chunk but the last.</param>
/// <param name="UserId">Who started it.</param>
/// <param name="Overwrite">Whether the upload may replace a file that holds its name (the <c>overwrite</c> field of
/// its first chunk). A manifest written before the field existed reads as true: those uploads replaced.</param>
/// <param name="FileId">The id the client gave the upload (the store key of a dataset upload is made from it and the
/// user, §7.1). Null for a share batch's upload and for a manifest written before the field existed.</param>
/// <param name="SourceFileName">The name of the file on the client's disk, from the first chunk: the Hub matches a file
/// that the user selects again to an open upload by it (§7.1). It can differ from the name in <paramref name="Path"/>
/// (Keep both).</param>
/// <param name="LastModified">The file's last-modified time on the client (milliseconds since 1970), from the first chunk.</param>
public sealed record ChunkManifest(string Path, long TotalSize, int TotalChunks, long ChunkSize, string? UserId,
    bool Overwrite = true, string? FileId = null, string? SourceFileName = null, long? LastModified = null)
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
