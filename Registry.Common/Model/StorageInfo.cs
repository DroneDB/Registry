namespace Registry.Common.Model;

/// <summary>
/// Simple record for disk total size and free space.
/// </summary>
public class StorageInfo
{
    public StorageInfo(long totalSize, long freeSpace)
    {
        TotalSize = totalSize;
        FreeSpace = freeSpace;
    }

    public long FreeSpace { get; }
    public float FreeSpacePerc => (float)FreeSpace / TotalSize;
    public long TotalSize { get; }
}