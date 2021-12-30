using Newtonsoft.Json;

namespace Registry.Web.Models.DTO
{
    public class MetaListItemDto
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }
    }
}