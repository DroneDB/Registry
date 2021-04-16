using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Registry.Common;

namespace Registry.Ports.DroneDB.Models
{
    public class DdbAttributes
    {
        private readonly IDdb _ddb;

        public DdbAttributes(IDdb ddb)
        {
            _ddb = ddb;
        }

        #region Meta

        private string MetaRaw
        {
            get => JsonConvert.SerializeObject(_meta);
            set => _meta = JsonConvert.DeserializeObject<Dictionary<string, object>>(value);
        }

        private Dictionary<string, object> _meta;

        private const string LastUpdateField = "mtime";
        private const string PublicMetaField = "public";

        public bool IsPublic
        {
            get => SafeGetMetaField<bool>(PublicMetaField);
            set => SafeSetMetaField(PublicMetaField, value);
        }

        public DateTime? LastUpdate
        {
            get
            {
                var val = SafeGetMetaField<long?>(LastUpdateField);

                if (val == null) return null;

                var dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(val.Value);

                return dateTimeOffset.LocalDateTime;

            }
            set
            {
                if (value == null)
                {
                    SafeSetMetaField<long?>(LastUpdateField, null);
                    return;
                }

                var dt = new DateTimeOffset(value.Value);

                SafeSetMetaField(LastUpdateField, dt.ToUnixTimeSeconds());
            }
        }

        public Dictionary<string, object> Meta => new(_meta);

        private void UpdateMeta()
        {
            _meta = _ddb.GetAttributesRaw();
        }

        private void SafeSetMetaField<T>(string field, T val)
        {

            if (_meta == null)
            {
                _meta = new Dictionary<string, object>
                {
                    { field, val }
                };
                return;
            }

            if (_meta.ContainsKey(field))
                _meta[field] = val;
            else
                _meta.Add(field, val);

            SaveMeta();

        }

        private void SaveMeta()
        {
            _meta = _ddb.ChangeAttributesRaw(_meta);
        }

        private T SafeGetMetaField<T>(string field)
        {
            UpdateMeta();

            var res = _meta?.SafeGetValue(field);
            if (!(res is T)) return default;

            return (T)res;
        }
        #endregion
    }
}