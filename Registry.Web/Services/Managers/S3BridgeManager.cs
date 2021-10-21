using System;
using System.IO;
using System.Runtime.Caching;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.ObjectSystem;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers
{
    public class S3BridgeManager : IS3BridgeManager
    {
        private readonly IObjectSystem _objectSystem;
        private readonly IOptions<AppSettings> _settings;
        private readonly ILogger<IS3BridgeManager> _logger;
        private readonly ObjectCache _objectCache;
        private readonly TimeSpan _cacheExpiration;
        private readonly TimeSpan DefaultBridgeCacheExpiration = new(0, 30, 0);


        public S3BridgeManager(IObjectSystem objectSystem, ObjectCache objectCache, IOptions<AppSettings> settings,
            ILogger<IS3BridgeManager> logger)
        {
            _objectSystem = objectSystem;
            _objectCache = objectCache;
            _settings = settings;
            _logger = logger;
            _cacheExpiration = settings.Value.BridgeCacheExpiration ?? DefaultBridgeCacheExpiration;
        }

        public Task<bool> ObjectExists(string bucketName, string path)
        {
            if (string.IsNullOrWhiteSpace(bucketName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(bucketName));
            
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(path));
            
            return _objectSystem.ObjectExistsAsync(bucketName, path);
        }

        public Task GetObjectStream(string bucketName, string objectName, long offset, long length, Action<Stream> cb)
        {
            if (cb == null) throw new ArgumentNullException(nameof(cb));
            
            if (string.IsNullOrWhiteSpace(bucketName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(bucketName));
            
            if (string.IsNullOrWhiteSpace(objectName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(objectName));
            
            return _objectSystem.GetObjectAsync(bucketName, objectName, offset, length, cb);
        }

        public async Task<string> GetObject(string bucketName, string objectName)
        {
            if (string.IsNullOrWhiteSpace(bucketName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(bucketName));
            
            var key = GetObjectKey(bucketName, objectName);
            
            // We should only cache files inside .build, otherwise we can absolutely shoot ourselves in the foot
            var itm = _objectCache.Get(key);
            
            string filePath;
                
            if (itm != null)
            {
                filePath = (string)itm;
            }
            else
            {
                var tmpFile = Path.GetTempFileName();
                try
                {
                    await _objectSystem.GetObjectAsync(bucketName, objectName, tmpFile);
                    _objectCache.Set(key, tmpFile, new CacheItemPolicy { SlidingExpiration = _cacheExpiration});
                }
                finally
                {
                    CommonUtils.SafeDelete(tmpFile);
                }

                filePath = (string)_objectCache.Get(key);
            }

            return filePath;
        }

        public Task RemoveObjectFromCache(string bucketName, string objectName)
        {
            if (string.IsNullOrWhiteSpace(bucketName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(bucketName));
            
            if (string.IsNullOrWhiteSpace(objectName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(objectName));

            return Task.Run(() =>
            {
                var key = GetObjectKey(bucketName, objectName);

                _objectCache.Remove(key);
            });
        }

        public bool IsS3Based()
        {
            return _objectSystem.IsS3Based();
        }

        private string GetObjectKey(string bucketName, string objectName)
        {
            return $"Obj-{bucketName}-{objectName}";
        }
    }
}