using Registry.Adapters.Ddb.Model;
using Registry.Common;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Models
{
    public class StorageEntryDto : StorageFileDto
    {
        public EntryType Type { get; set; }
        public string Hash { get; set; }
        public long Size { get; set; }
    }
}