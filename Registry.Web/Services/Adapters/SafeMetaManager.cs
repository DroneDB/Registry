using System;
using Registry.Adapters.DroneDB;
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
            get
            {
                try
                {
                    return  _manager.Get<string>(NameField);
                }
                catch (DDBException)
                {
                    return null;
                }
            }
            set
            {
                if (value == null)
                    throw new ArgumentException("Name cannot be null");

                _manager.Set(NameField, value);
            }
        }

        public int? Entries
        {
            get
            {
                try
                {
                    return _manager.Get<int>(ObjectsCountField);
                }
                catch (DDBException)
                {
                    return null;
                }
            }
        }

        public Visibility? Visibility
        {
            get
            {

                try
                {
                    return (Visibility)_manager.Get<int>(VisibilityField);
                }
                catch (DDBException)
                {
                    return null;
                }
            }
            set
            {
                if (value == null)
                    throw new ArgumentException("Visibility cannot be null");
                _manager.Set(VisibilityField, ((int)value).ToString());
            }
        }

        public bool? IsPublic
        {
            get
            {
                try
                {
                    return _manager.Get<bool>(PublicField);
                }
                catch (DDBException)
                {
                    return null;
                }
            }
            set
            {
                if (value == null) throw new InvalidOperationException("Cannot set null value");
                _manager.Set(PublicField, value.Value ? "1" : "0");
            }
        }

        public DateTime? LastUpdate
        {
            get
            {
                try
                {
                    var unixTime = _manager.Get<int>(LastUpdateField);

                    var dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(unixTime);

                    return dateTimeOffset.LocalDateTime;
                }
                catch (DDBException)
                {
                    return null;
                }
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