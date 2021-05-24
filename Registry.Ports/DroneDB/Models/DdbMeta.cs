using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Registry.Common;

namespace Registry.Ports.DroneDB.Models
{
    public class DdbMeta
    {
        protected Dictionary<string, object> _meta;

        public DdbMeta(Dictionary<string, object> meta)
        {
            _meta = meta;
        }

        public const string LastUpdateField = "mtime";
        public const string PublicMetaField = "public";
        public const string ObjectsCountField = "entries";
        
        public bool IsPublic => SafeGetMetaField<bool>(PublicMetaField);

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
            var res = _meta?.SafeGetValue(field);
            if (!(res is T)) return default;

            return (T)res;
        }

        protected string MetaRaw
        {
            get => JsonConvert.SerializeObject(_meta);
            set => _meta = JsonConvert.DeserializeObject<Dictionary<string, object>>(value);
        }

        public Dictionary<string, object> Meta => new(_meta);


    }
}
