using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO;

/// <summary>The attributes of the obsolete <c>chattr</c> endpoint.</summary>
public class AttributesDto
{
    /// <summary>The dataset is public (its visibility is Public) or not (Private); null: unchanged.</summary>
    [JsonPropertyName("public")]
    public bool? IsPublic { get; set; }
}
