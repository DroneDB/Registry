using DDB.Bindings.Model;
using Newtonsoft.Json;

namespace Registry.Adapters.Ddb.Model
{
    public class Delta
    {
        [JsonProperty("adds")]
        public AddAction[] Adds { get; set; }

        [JsonProperty("removes")]
        public RemoveAction[] Removes { get; set; }
    }
}