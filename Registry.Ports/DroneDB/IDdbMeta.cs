namespace Registry.Ports.DroneDB;

/// <summary>
/// Dataset-level metadata and self-description: tag operations, info commands, size and stamp.
/// </summary>
public interface IDdbMeta
{
    IMetaManager Meta { get; }

    Entry GetInfo();
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

    /// <summary>
    /// Moves the committed changes of the index from its WAL into the database file and empties
    /// the WAL (DDBCheckpoint). Index writes leave their changes in the WAL, so this comes first
    /// whenever the database file is copied or archived. False when another connection kept the
    /// checkpoint from completing: the WAL then still holds part of the data.
    /// </summary>
    bool Checkpoint();
}
