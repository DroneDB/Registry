using System;
using Newtonsoft.Json.Linq;

namespace Registry.Ports.DroneDB.Models
{
    public class DdbMeta
    {
        public string Id { get; set; }

        public JObject Data { get; set; }

        public DateTime ModifiedTime { get; set; }
    }
}