using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;

namespace Registry.Web.Services.Adapters
{
    public class DummyDistributedCache :IDistributedCache
    {
        public byte[] Get(string key)
        {
            return null;
        }

        public Task<byte[]> GetAsync(string key, CancellationToken token = new CancellationToken())
        {
            return Task.FromResult((byte[])null);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = new CancellationToken())
        {
            return new Task(() => {});
        }

        public void Refresh(string key)
        {
            
        }

        public Task RefreshAsync(string key, CancellationToken token = new CancellationToken())
        {
            return new Task(() => { });
        }

        public void Remove(string key)
        {
            
        }

        public Task RemoveAsync(string key, CancellationToken token = new CancellationToken())
        {
            return new Task(() => { });
        }
    }
}
