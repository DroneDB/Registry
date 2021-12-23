using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Registry.Ports.DroneDB.Models
{

    public class Entry
    {
        public string Path { get; set; }
        public string Hash { get; set; }
        public EntryType Type { get; set; }

        public Dictionary<string, object> Properties { get; set; }

        public DateTime ModifiedTime { get; set; }

        public long Size { get; set; }
        public int Depth { get; set; }

        public JObject PointGeometry { get; set; }

        public JObject PolygonGeometry { get; set; }
    }

}
