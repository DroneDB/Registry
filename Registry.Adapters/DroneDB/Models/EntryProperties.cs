using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Registry.Common;

namespace Registry.Adapters.DroneDB.Models
{
    public class EntryProperties
    {
        protected Dictionary<string, object> _properties;

        public EntryProperties(Dictionary<string, object> properties)
        {
            _properties = properties;
        }

        public const string LastUpdateField = "mtime";
        public const string PublicPropertyField = "public";
        public const string ObjectsCountField = "entries";
        
        public bool IsPublic => SafeGetMetaField<bool>(PublicPropertyField);

        public int ObjectsCount => SafeGetMetaField<int>(ObjectsCountField);

        public DateTime? LastUpdate
        {
            get
            {
                var val = SafeGetMetaField<long?>(LastUpdateField);

                if (val == null) return null;

                var dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(val.Value);

                return dateTimeOffset.LocalDateTime;
            }
        }

        protected T SafeGetMetaField<T>(string field)
        {
            var res = _properties?.SafeGetValue(field);
            if (!(res is T)) return default;

            return (T)res;
        }

        protected string MetaRaw
        {
            get => JsonConvert.SerializeObject(_properties);
            set => _properties = JsonConvert.DeserializeObject<Dictionary<string, object>>(value);
        }

        public Dictionary<string, object> Properties => new(_properties);


    }
}
