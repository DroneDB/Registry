namespace Registry.Common.Model
{
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
}
