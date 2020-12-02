using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Caching.Distributed;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Services.Adapters
{
    public class CachedDdb : IDdb
    {
        private readonly IDdb _ddb;
        private readonly IDistributedCache _cache;
        private readonly TimeSpan? _expiration;

        // TODO: Good candidate for new config key
        private readonly TimeSpan CacheExpireTime = new TimeSpan(0,5,0);

        public CachedDdb(IDdb ddb, IDistributedCache cache, TimeSpan? expiration)
        {
            _ddb = ddb;
            _cache = cache;
            _expiration = expiration;
        }
        public IEnumerable<DdbEntry> Search(string path)
        {
            return _ddb.Search(path);
        }

        public void Add(string path, byte[] data)
        {
            _ddb.Add(path, data);
        }

        public void Add(string path, Stream data)
        {
            _ddb.Add(path, data);
        }

        public void Remove(string path)
        {
            _ddb.Remove(path);
        }

        public Dictionary<string, object> ChangeAttributes(Dictionary<string, object> attributes)
        {
            return _ddb.ChangeAttributes(attributes);
        }

        public void GenerateThumbnail(string imagePath, int size, string outputPath)
        {

            var key = $"Thumb-{CommonUtils.ComputeFileHash(imagePath)}";
            var res = _cache.Get(key);

            if (res != null) {
                File.WriteAllBytes(outputPath, res);
                return;
            }

            _ddb.GenerateThumbnail(imagePath, size, outputPath);

            var options = new DistributedCacheEntryOptions
            {
                SlidingExpiration = _expiration ?? CacheExpireTime
            };

            _cache.Set(key, File.ReadAllBytes(outputPath), options);
        }
    }
}