using System;
using System.Collections.Generic;

namespace Registry.Ports.DroneDB.Models
{
    public class DdbAttributes : DdbProperties
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
                UpdateProperties();

                return base.IsPublic;
            }
            set => SafeSetPropertyField(PublicPropertyField, value);
        }

        public new int ObjectsCount
        {
            get
            {
                UpdateProperties();

                return base.ObjectsCount;
            }
        }

        public new DateTime? LastUpdate
        {
            get
            {
                UpdateProperties();

                return base.LastUpdate;

            }
            set
            {
                if (value == null)
                {
                    SafeSetPropertyField<long?>(LastUpdateField, null);
                    return;
                }

                var dt = new DateTimeOffset(value.Value);

                SafeSetPropertyField(LastUpdateField, dt.ToUnixTimeSeconds());
            }
        }


        private void UpdateProperties()
        {
            _properties = _ddb.GetAttributesRaw();
        }

        private void SafeSetPropertyField<T>(string field, T val)
        {

            if (_properties == null)
            {
                _properties = new Dictionary<string, object>
                {
                    { field, val }
                };
                return;
            }

            if (_properties.ContainsKey(field))
                _properties[field] = val;
            else
                _properties.Add(field, val);

            SaveProperties();

        }

        private void SaveProperties()
        {

            var tmp = new Dictionary<string, object>();

            // We can only set LastUpdate and IsPublic
            if (_properties.ContainsKey(LastUpdateField))
                tmp.Add(LastUpdateField, _properties[LastUpdateField]);

            if (_properties.ContainsKey(PublicPropertyField))
                tmp.Add(PublicPropertyField, _properties[PublicPropertyField]);

            _properties = _ddb.ChangeAttributesRaw(tmp);
        }


        #endregion
    }
}