using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Registry.Web.Services.Adapters;

namespace Registry.Web.HealthChecks;

public class CacheHealthCheck : IHealthCheck
{
    private readonly IDistributedCache _cache;

    public CacheHealthCheck(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
    {
        var testKey = Guid.NewGuid().ToString();

        var data = new Dictionary<string, object> { { "TestKey", testKey }, { "Provider", _cache.GetType().FullName } };

        try
        {

            var res = await _cache.GetAsync(testKey, cancellationToken);
            if (res != null)
                return HealthCheckResult.Unhealthy("Cache not working properly", null, data);

            await _cache.SetStringAsync(testKey, "test", cancellationToken);

            res = await _cache.GetAsync(testKey, cancellationToken);

            if (res == null)
                return HealthCheckResult.Unhealthy("Cache not working properly: cannot store data", null, data);

            await _cache.RemoveAsync(testKey, cancellationToken);

            res = await _cache.GetAsync(testKey, cancellationToken);

            return res != null ?
                HealthCheckResult.Unhealthy("Cache not working properly: cannot delete test key", null, data) :
                HealthCheckResult.Healthy("Cache is working properly", data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Exception while testing cache: " + ex.Message, ex, data);
        }
    }
}