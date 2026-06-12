#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace Registry.Ports.Archives;

/// <summary>
/// Format-agnostic abstraction over a compressed-archive reader. Implementations
/// (e.g. SharpCompress) live in <c>Registry.Adapters</c> so no third-party archive
/// type leaks into the application layer. Consumed by the <c>archive-extract</c>
/// heavy tool to enumerate and stream the entries of an archive stored in a dataset.
/// </summary>
public interface IArchiveExtractor
{
    /// <summary>True when the file name maps to a supported archive format.</summary>
    bool IsSupported(string fileName);

    /// <summary>
    /// Opens the archive at <paramref name="archivePath"/> and exposes its entries for
    /// sequential iteration. The returned session must be disposed by the caller.
    /// </summary>
    IArchiveReadSession Open(string archivePath);
}

/// <summary>A read session over an opened archive. Dispose to release the file handle.</summary>
public interface IArchiveReadSession : IDisposable
{
    /// <summary>Number of non-directory entries (used to compute progress).</summary>
    int FileEntryCount { get; }

    /// <summary>
    /// Estimated total uncompressed size in bytes (used for the quota / disk-space
    /// / decompression-bomb guard). Null when the format does not expose it cheaply.
    /// </summary>
    long? TotalUncompressedBytes { get; }

    /// <summary>Enumerates the entries in sequential order (efficient for solid rar/7z).</summary>
    IEnumerable<ArchiveEntry> Entries();
}

/// <summary>A single archive entry exposed to the consumer.</summary>
public sealed record ArchiveEntry(
    string Key,
    bool IsDirectory,
    long Size,
    DateTime? LastModified)
{
    /// <summary>
    /// Opens a read stream over the current entry's uncompressed content.
    /// The returned stream must be disposed by the caller.
    /// </summary>
    public required Func<Stream> OpenStream { get; init; }
}
