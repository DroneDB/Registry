using Newtonsoft.Json;

namespace Registry.Adapters.DroneDB.Models
{
    public class Delta
    {
        [JsonProperty("adds")]
        public AddAction[] Adds { get; set; }

        [JsonProperty("removes")]
        public RemoveAction[] Removes { get; set; }
    }
}