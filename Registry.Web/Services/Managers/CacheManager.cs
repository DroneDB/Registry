using System;
using System.IO;
using System.Runtime.Caching;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers
{
    public class CacheManager : ICacheManager
    {
        private readonly ObjectCache _cache;

        //private readonly TimeSpan _defaultCacheExpireTime = new TimeSpan(0, 5, 0);

        private readonly TimeSpan DefaultThumbnailsCacheExpiration = new TimeSpan(0, 30, 0);
        private readonly TimeSpan DefaultTilesCacheExpiration = new TimeSpan(0, 30, 0);

        private readonly TimeSpan _thumbnailsCacheExpiration;
        private readonly TimeSpan _tilesCacheExpiration;
        
        public CacheManager(ObjectCache cache, IOptions<AppSettings> settings)
        {
            _cache = cache;

            _thumbnailsCacheExpiration = settings.Value.ThumbnailsCacheExpiration ?? DefaultThumbnailsCacheExpiration;
            _tilesCacheExpiration = settings.Value.TilesCacheExpiration ?? DefaultTilesCacheExpiration;

        }
        
        public async Task<byte[]> GenerateTile(IDdb ddb, string sourcePath, string sourceHash, int tz, int tx, int ty, bool retina)
        {
            var key = $"Tile-{sourceHash}-{tz}-{tx}-{ty}-{retina}";
            var res = _cache.Get(key);

            if (res != null)
                return await File.ReadAllBytesAsync((string)res);
            
            var tile = await ddb.GenerateTileAsync(sourcePath, tz, tx, ty, retina, sourceHash);

            var tmpFile = Path.GetTempFileName();

            try
            {
                await File.WriteAllBytesAsync(tmpFile, tile);
                _cache.Set(key, tmpFile, new CacheItemPolicy { SlidingExpiration = _tilesCacheExpiration});
            }
            finally
            {
                CommonUtils.SafeDelete(tmpFile);
            }
            
            return tile;
        }

        public async Task<byte[]> GenerateThumbnail(IDdb ddb, string sourcePath, string sourceHash, int size)
        {
            var key = $"Thumb-{sourceHash}-{size}";
            var res = _cache.Get(key);

            if (res != null)
                return await File.ReadAllBytesAsync((string)res);

            var thumb = await ddb.GenerateThumbnailAsync(sourcePath, size);

            var tmpFile = Path.GetTempFileName();

            try
            {
                await File.WriteAllBytesAsync(tmpFile, thumb);
                _cache.Set(key, tmpFile, new CacheItemPolicy { SlidingExpiration = _thumbnailsCacheExpiration});
            }
            finally
            {
                CommonUtils.SafeDelete(tmpFile);
            }

            return thumb;
        }

    }
}