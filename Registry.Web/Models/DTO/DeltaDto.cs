using Registry.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Models.DTO
{
    
    public class DeltaDto
    {
        [JsonProperty("adds")]
        public AddActionDto[] Adds { get; set; }

        [JsonProperty("removes")]
        public RemoveAction[] Removes { get; set; }
    }
    
    public class AddActionDto
    {
        [JsonProperty("path")]
        public string Path { get;  }

        [JsonProperty("hash")]
        public string Hash { get;  }

        public AddActionDto(string path, string hash)
        {
            Path = path;
            Hash = hash;
        }

        public override string ToString()
        {
            return $"ADD -> [{(string.IsNullOrEmpty(Hash) ? 'D' : 'F')}] {Path}";
        }
    }
    
    public class RemoveActionDto
    {
        [JsonProperty("path")]
        public string Path { get; }

        [JsonProperty("hash")]
        public string Hash { get; }

        public RemoveActionDto(string path, string hash)
        {
            Path = path;
            Hash = hash;
        }
        public override string ToString()
        {
            return $"DEL -> [{(string.IsNullOrEmpty(Hash) ? 'D' : 'F')}] {Path}";
        }

    }

}
