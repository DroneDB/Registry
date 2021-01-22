namespace Registry.Adapters.ObjectSystem.Model
{
    public class SyncFilesRes
    {
        public string[] SyncedFiles { get; set; }
        public SyncFileError[] ErrorFiles { get; set; }
    }
}