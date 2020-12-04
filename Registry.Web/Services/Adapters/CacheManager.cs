using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters
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

        public void GenerateThumbnail(IDdb ddb, string sourcePath, int size, string destPath, Func<Task> getData)
        {

            var key = $"Thumb-{CommonUtils.ComputeFileHash(sourcePath)}";
            var res = _cache.Get(key);

            if (res != null)
            {
                File.WriteAllBytes(destPath, res);
                return;
            }

            ddb.GenerateThumbnail(sourcePath, size, destPath);

            var options = new DistributedCacheEntryOptions
            {
                SlidingExpiration = _expiration
            };

            _cache.Set(key, File.ReadAllBytes(destPath), options);

        }
    }
}
