using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

public class AttributesDto
{
    [JsonProperty("public")]
    public bool IsPublic { get; set; }
}