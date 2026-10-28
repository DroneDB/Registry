using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Web.Models.Configuration;

public class CacheProvider
{

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CacheType Type { get; set; }
    public Dictionary<string, object>? Settings { get; set; }
}