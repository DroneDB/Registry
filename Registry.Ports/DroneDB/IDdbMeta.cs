namespace Registry.Ports.DroneDB;

/// <summary>
/// Dataset-level metadata and self-description: tag operations, info commands, size and stamp.
/// See ImproveParallelWrites plan, workstream 04 §7.
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
}
