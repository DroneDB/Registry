using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Registry.Ports.DroneDB.Models;

public class MetaDump : Meta
{
    [JsonProperty("path")]
    public string Path { get; set; }

    [JsonProperty("key")]
    public string Key { get; set; }

}