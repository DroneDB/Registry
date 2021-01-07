using Registry.Common;
using Registry.Web.Models.DTO;

namespace Registry.Web.Models
{
    public class ObjectRes
    {
        public byte[] Data { get; set; }
        public string Name { get; set; }
        public string ContentType { get; set; }

        public EntryType Type { get; set; }
        public string Hash { get; set; }
        public long Size { get; set; }
    }
}