using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Adapters
{
    /// <summary>
    /// Centralized meta manager to prevent magic strings proliferation
    /// </summary>
    public class SafeMetaManager
    {

        public const string NameField = "name";
        
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
    }
}