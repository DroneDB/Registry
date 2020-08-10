using Registry.Web.Models.DTO;

namespace Registry.Web.Models
{
    public class ObjectRes
    {
        public byte[] Data { get; set; }
        public string Name { get; set; }
        public string ContentType { get; set; }

        public ObjectType Type { get; set; }

    }
}