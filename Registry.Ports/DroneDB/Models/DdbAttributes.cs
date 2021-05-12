using System;
using System.Collections.Generic;

namespace Registry.Ports.DroneDB.Models
{
    public class DdbAttributes : DdbMeta
    {
        private readonly IDdb _ddb;

        public DdbAttributes(IDdb ddb) : base(ddb.GetAttributesRaw()) 
        {
            _ddb = ddb;
        }

        #region Meta

        public new bool IsPublic
        {
            get
            {
                UpdateMeta();

                return base.IsPublic;
            }
            set => SafeSetMetaField(PublicMetaField, value);
        }

        public new int ObjectsCount
        {
            get
            {
                UpdateMeta();

                return base.ObjectsCount;
            }
        }

        public new DateTime? LastUpdate
        {
            get
            {
                UpdateMeta();

                return base.LastUpdate;

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

            var tmp = new Dictionary<string, object>();

            // We can only set LastUpdate and IsPublic
            if (_meta.ContainsKey(LastUpdateField))
                tmp.Add(LastUpdateField, _meta[LastUpdateField]);

            if (_meta.ContainsKey(PublicMetaField))
                tmp.Add(PublicMetaField, _meta[PublicMetaField]);

            _meta = _ddb.ChangeAttributesRaw(tmp);
        }


        #endregion
    }
}