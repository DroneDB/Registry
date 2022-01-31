using System;
using System.Collections.Generic;

namespace Registry.Ports.DroneDB.Models
{
    public class EntryAttributes : EntryProperties
    {
        private readonly IDDB _ddb;

        public EntryAttributes(IDDB ddb) : base(ddb.GetAttributesRaw()) 
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
            set => SafeSetPropertyField(PublicField, value);
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

            // We can only set IsPublic
            if (_properties.ContainsKey(PublicField))
                tmp.Add(PublicField, _properties[PublicField]);

            _properties = _ddb.ChangeAttributesRaw(tmp);
        }


        #endregion
    }
}