using System.Collections.Generic;
using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

public class StacConformanceDto
{
    [JsonProperty("conformsTo")]
    public IEnumerable<string> ConformsTo { get; set; }
}
