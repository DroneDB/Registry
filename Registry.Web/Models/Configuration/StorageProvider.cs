using System.Collections.Generic;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Registry.Web.Models.Configuration
{
    public class StorageProvider
    {

        [JsonConverter(typeof(StringEnumConverter))] 
        public StorageType Type { get; set; }
        public Dictionary<string, object> Settings { get; set; }
    }
}