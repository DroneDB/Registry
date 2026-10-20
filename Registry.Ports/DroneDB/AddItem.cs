#nullable enable
using System;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The SHA-256 of a file computed by whoever wrote it (an upload is hashed while it streams to
/// disk), with the size and modification time the file had right after the write. DroneDB uses
/// the hash only while the file still has that size and time; otherwise it hashes the file.
/// </summary>
/// <param name="Sha256">Lowercase hex digest.</param>
/// <param name="Size">Bytes written.</param>
/// <param name="ModifiedUtc">Last write time of the file after the write.</param>
public sealed record FileHashHint(string Sha256, long Size, DateTime ModifiedUtc);

/// <summary>A file to index, with the hash its writer computed, if any.</summary>
/// <param name="Path">The file path (dataset-relative for <see cref="IDdbIndex"/>, absolute for
/// <see cref="IDdbWrapper"/>).</param>
/// <param name="Hint">The hash computed while writing the file, or null.</param>
public sealed record AddItem(string Path, FileHashHint? Hint = null);
