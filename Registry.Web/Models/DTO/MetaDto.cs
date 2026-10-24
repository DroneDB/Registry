using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Registry.Common;
using Registry.Ports;

namespace Registry.Web.Models.DTO;

public class MetaDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("mtime")]
    [JsonConverter(typeof(SecondEpochConverter))]
    public DateTime ModifiedTime { get; set; }
}