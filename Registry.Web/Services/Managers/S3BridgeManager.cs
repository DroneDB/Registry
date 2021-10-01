using Registry.Web.Models.Configuration;
using Microsoft.Extensions.Options;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers
{
    public class S3BridgeManager : IS3BridgeManager
    {
        IOptions<AppSettings> _settings;

        public S3BridgeManager(IOptions<AppSettings> settings)
        {
            _settings = settings;
        }

        public bool IsS3Enabled()
        {
            return _settings.Value.StorageProvider.Type == StorageType.CachedS3 || _settings.Value.StorageProvider.Type == StorageType.S3;
        }
    }
}
