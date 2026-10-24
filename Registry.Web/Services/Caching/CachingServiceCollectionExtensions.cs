#nullable enable
using Microsoft.Extensions.DependencyInjection;

namespace Registry.Web.Services.Caching;

/// <summary>Registration of the record cache and the cache generations (over the registered distributed cache).</summary>
public static class CachingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IRecordCache"/> (single-flight and fail-open get-or-create of records) and
    /// <see cref="ICacheGenerations"/>, both process-wide over the registered <c>IDistributedCache</c>.
    /// </summary>
    public static IServiceCollection AddRecordCache(this IServiceCollection services)
    {
        services.AddSingleton<IRecordCache, RecordCache>();
        services.AddSingleton<ICacheGenerations, CacheGenerations>();
        return services;
    }
}
