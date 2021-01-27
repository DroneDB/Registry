using Registry.Ports.ObjectSystem.Model;

namespace Registry.Adapters.ObjectSystem.Model
{
    public class BucketInfoDto
    {
        public string Name { get; set; }
        public string Owner { get; set; }
        public ObjectInfoDto[] Objects { get; set; }
    }
}