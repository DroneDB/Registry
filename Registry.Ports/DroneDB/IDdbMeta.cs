namespace Registry.Ports.DroneDB;

/// <summary>
/// Dataset-level metadata and self-description: tag operations, info commands, size and stamp.
/// </summary>
public interface IDdbMeta
{
    IMetaManager Meta { get; }

    Entry GetInfo();

    /// <summary>
    /// Calls DDB info command on specified path
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    Entry GetInfo(string path);

    long GetSize();
    Stamp GetStamp();

    /// <summary>
    /// Whether the index has no entries (one aggregate query, no listing).
    /// </summary>
    bool IsEmpty();

    /// <summary>
    /// Changes whenever the index is written: size and write time of the index database and its
    /// WAL file. Null when the index files cannot be read.
    /// </summary>
    string? GetIndexFingerprint();
}
