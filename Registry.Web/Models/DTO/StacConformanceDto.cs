#nullable disable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO;

public class StacConformanceDto
{
    [JsonPropertyName("conformsTo")]
    public IEnumerable<string> ConformsTo { get; set; }
}
