using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Services
{
    public static class DistributedCacheHelpers
    {
        public static IDdb UseCache(this IDdb ddb, IDistributedCache distributedCache, CacheProviderSettings settings)
        {
            return new CachedDdb(ddb, distributedCache, settings?.Expiration);
        }
    }
}
