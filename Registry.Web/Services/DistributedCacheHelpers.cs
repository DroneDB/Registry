using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Services
{
    public static class DistributedCacheHelpers
    {
        public static IDdb UseCache(this IDdb ddb, IDistributedCache distributedCache)
        {
            return new CachedDdb(ddb, distributedCache);
        }
    }
}
