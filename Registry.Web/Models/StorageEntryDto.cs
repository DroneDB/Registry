using Registry.Adapters.DroneDB.Models;

namespace Registry.Web.Models
{
    public class StorageEntryDto : StorageFileDto
    {
        public EntryType Type { get; set; }
        public string Hash { get; set; }
        public long Size { get; set; }
    }
}