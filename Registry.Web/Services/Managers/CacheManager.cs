using System;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers
{
    public class CacheManager : ICacheManager
    {
        private class Carrier
        {
            public Func<object[], byte[]> GetData { get; set; }
            public TimeSpan Expiration { get; set; }
        }

        private readonly ObjectCache _cache;

        private readonly TimeSpan DefaultCacheExpiration = new(0, 30, 0);

        private readonly DictionaryEx<string, Carrier> _providers = new();

        public CacheManager(ObjectCache cache)
        {
            _cache = cache;
        }

        public void Register(string seed, Func<object[], byte[]> getData, TimeSpan? expiration = null)
        {
            _providers.Add(seed, new Carrier
            {
                Expiration = expiration ?? DefaultCacheExpiration, 
                GetData = getData
            });
        }

        public void Unregister(string seed)
        {
            _providers.Remove(seed);
        }

        public string MakeKey(string seed, string category, object[] parameters)
        {
            return parameters == null ? $"{seed}-{category}" : $"{seed}-{category}:{string.Join(",", parameters.Select(p => p.ToString()))}";
        }
        
        public void Remove(string seed, string category, params object[] parameters)
        {
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            if (category == null) throw new ArgumentNullException(nameof(category));
            
            _cache.Remove(MakeKey(seed, category, parameters));
        }

        public async Task<string> Get(string seed, string category, params object[] parameters)
        {
            if (seed == null) throw new ArgumentNullException(nameof(seed));

            if (!_providers.TryGetValue(seed, out var carrier))
                throw new ArgumentException("No provider registered for seed: " + seed);

            var key = MakeKey(seed, category, parameters);

            var res = _cache.Get(key);

            if (res != null)
                return (string)res;

            var data = carrier.GetData(parameters);

            var tmpFile = Path.GetTempFileName();

            try
            {
                await File.WriteAllBytesAsync(tmpFile, data);
                _cache.Set(key, tmpFile, new CacheItemPolicy { SlidingExpiration = carrier.Expiration });
            }
            finally
            {
                CommonUtils.SafeDelete(tmpFile);
            }

            return (string)_cache.Get(key);
        }
        
        public Task Clear(string seed, string category = null)
        {
            return Task.Run(() =>
            {
                var k = category != null ? MakeKey(seed, category, null) : seed;
                var keys = _cache.Where(o => o.Key.StartsWith(k)).Select(o => o.Key).ToArray();

                foreach (var key in keys)
                    _cache.Remove(key);
            });        
        }

    }
}