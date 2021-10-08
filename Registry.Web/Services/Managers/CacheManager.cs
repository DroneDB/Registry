using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers
{
    public class CacheManager : ICacheManager
    {
        private readonly IDistributedCache _cache;
        private readonly AppSettings _settings;
        private readonly TimeSpan _defaultCacheExpireTime = new TimeSpan(0, 5, 0);

        private readonly TimeSpan _expiration;

        public CacheManager(IDistributedCache cache, IOptions<AppSettings> settings)
        {
            _cache = cache;
            _settings = settings.Value;

            var cacheSettings = _settings.CacheProvider?.Settings.ToObject<CacheProviderSettings>();

            _expiration = cacheSettings != null
                ? (cacheSettings.Expiration.TotalSeconds > 1 ? cacheSettings.Expiration : _defaultCacheExpireTime)
                : _defaultCacheExpireTime;
            
        }

        public async Task<byte []> GenerateThumbnail(IDdb ddb, string sourcePath, string sourceHash, int size)
        {

            var key = $"Thumb-{sourceHash}-{size}";
            var res = await _cache.GetAsync(key);

            if (res != null)
            {
                return res;
            }

            var options = new DistributedCacheEntryOptions
            {
                SlidingExpiration = _expiration
            };

            var thumb = await ddb.GenerateThumbnailAsync(sourcePath, size);
            await _cache.SetAsync(key, thumb, options);

            return thumb;

        }
    }
}
