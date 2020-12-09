using System.Collections.Generic;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Registry.Web.Models.Configuration
{
    public class CacheProvider
    {

        [JsonConverter(typeof(StringEnumConverter))]
        public CacheType Type { get; set; }
        public Dictionary<string, object> Settings { get; set; }
    }
}