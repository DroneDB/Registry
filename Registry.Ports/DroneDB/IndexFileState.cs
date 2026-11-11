using System;

namespace Registry.Ports.DroneDB;

/// <summary>
/// What the files of a dataset's index say about its writes, read with one stat pass.
/// </summary>
/// <param name="Fingerprint">Changes whenever the index is written: size and write time of the
/// database and of its WAL file.</param>
/// <param name="LastWriteUtc">When the changes last reached the index files: the newer of the database
/// write time and the WAL write time (a WAL without bytes is left out: readers create an empty one).
/// Reads leave it unchanged; libddb leaves a write in the WAL until a later flush rewrites the database,
/// so it can trail the write itself by a short time.</param>
public sealed record IndexFileState(string Fingerprint, DateTime LastWriteUtc);

public static class IndexFileStateExtensions
{
    /// <summary>
    /// The fingerprint of <see cref="IDdbMeta.GetIndexFileState"/>: null when the index files cannot be read.
    /// </summary>
    public static string? GetIndexFingerprint(this IDdbMeta meta) => meta.GetIndexFileState()?.Fingerprint;
}
