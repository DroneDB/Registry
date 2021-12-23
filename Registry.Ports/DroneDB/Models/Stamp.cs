using System.Collections.Generic;

namespace Registry.Ports.DroneDB.Models
{

    public class Stamp
    {
        public string Checksum { get; set; }

        public List<Dictionary<string,string>> Entries { get; set; }

    }

}