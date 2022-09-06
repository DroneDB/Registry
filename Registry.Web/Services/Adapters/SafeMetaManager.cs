using System;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Services.Adapters
{
    /// <summary>
    /// Centralized meta manager to prevent magic strings proliferation
    /// </summary>
    public class SafeMetaManager
    {
        public const string NameField = "name";
        public const string LastUpdateField = "mtime";
        public const string PublicField = "public";
        public const string VisibilityField = "visibility";
        public const string ObjectsCountField = "entries";

        private readonly IMetaManager _manager;

        public SafeMetaManager(IMetaManager manager)
        {
            _manager = manager;
        }

        public string Name
        {
            get => _manager.Get(NameField);
            set => _manager.Set(NameField, value);
        }

        public int Entries => CommonUtils.SafeParse(_manager.Get(ObjectsCountField)) ?? 0;

        public Visibility? Visibility
        {
            get
            {
                var val = CommonUtils.SafeParse(_manager.Get(VisibilityField));
                return val == null ? null : (Visibility)val;
            }
            set
            {
                if (value == null)
                    throw new ArgumentException("Visibility cannot be null");
                _manager.Set(VisibilityField, ((int)value).ToString());
            }
        }

        public bool IsPublic
        {
            get
            {
                var val = CommonUtils.SafeParse(_manager.Get(PublicField));
                return val != null && val == 1;
            }
            set => _manager.Set(PublicField, value ? "1" : "0");
        }

        public DateTime? LastUpdate
        {
            get
            {
                var val = _manager.Get(LastUpdateField);

                if (val == null) return null;

                if (!int.TryParse(val, out var unixTime)) return null;

                var dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(unixTime);

                return dateTimeOffset.LocalDateTime;
            }
            // Maybe this is not needed
            set
            {
                if (value == null)
                    throw new ArgumentException("LastUpdate cannot be null");
                
                var val = ((DateTimeOffset)value.Value).ToUnixTimeSeconds().ToString();
                _manager.Set(LastUpdateField, val);
            }
        }
    }
}