using Registry.Common.Model;
using Registry.Ports.DroneDB;

namespace Registry.Web.Models;

public class StorageEntryDto : StorageFileDto
{
    public EntryType Type { get; set; }
    public string Hash { get; set; }
    public long Size { get; set; }
}