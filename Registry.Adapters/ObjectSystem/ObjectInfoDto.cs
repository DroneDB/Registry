using System;
using System.Collections.Generic;

namespace Registry.Adapters.ObjectSystem
{
    public class ObjectInfoDto
    {
        public string Name { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string ETag { get; set; }
        public string ContentType { get; set; }
        public Dictionary<string, string> MetaData { get; set; }
    }
}