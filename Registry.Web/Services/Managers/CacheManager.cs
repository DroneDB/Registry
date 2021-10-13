using System;
using System.IO;
using System.Runtime.Caching;
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
        private readonly ObjectCache _cache;

        private readonly AppSettings _settings;
        //private readonly TimeSpan _defaultCacheExpireTime = new TimeSpan(0, 5, 0);

        private readonly TimeSpan _expiration = new TimeSpan(0, 30, 0);

        public CacheManager(ObjectCache cache, IOptions<AppSettings> settings)
        {
            _cache = cache;
            _settings = settings.Value;

            /*
            var cacheSettings = _settings.CacheProvider?.Settings.ToObject<CacheProviderSettings>();

            _expiration = cacheSettings != null
                ? (cacheSettings.Expiration.TotalSeconds > 1 ? cacheSettings.Expiration : _defaultCacheExpireTime)
                : _defaultCacheExpireTime;*/
        }

        public async Task<byte[]> GenerateThumbnail(IDdb ddb, string sourcePath, string sourceHash, int size)
        {
            var key = $"Thumb-{sourceHash}-{size}";
            var res = _cache.Get(key);

            if (res != null)
                return await File.ReadAllBytesAsync((string)res);

            var thumb = await ddb.GenerateThumbnailAsync(sourcePath, size);

            _cache.Set(key, thumb, DateTime.Now + _expiration);

            return thumb;
        }

        public async Task GenerateThumbnailStream(IDdb ddb, string sourcePath, string sourceHash, int size,
            Stream stream)
        {
            var key = $"Thumb-{sourceHash}-{size}";
            var res = _cache.Get(key);

            if (res != null)
            {
                await using (var file = File.OpenRead((string)res))
                    await file.CopyToAsync(stream);
            }

            var thumb = await ddb.GenerateThumbnailAsync(sourcePath, size);

            _cache.Set(key, thumb, DateTime.Now + _expiration);

            stream.Write(thumb);
        }
    }
}