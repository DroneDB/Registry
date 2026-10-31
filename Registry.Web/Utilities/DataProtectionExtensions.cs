using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Models.Configuration;
using StackExchange.Redis;

namespace Registry.Web.Utilities;

/// <summary>
/// Wires up ASP.NET Core Data Protection with a SHARED key ring so the web host and the processing
/// node can encrypt/decrypt the same protected payloads (import credentials): the cache's Redis when there
/// is one (no other store to run), otherwise a folder every process reads.
/// </summary>
public static class DataProtectionExtensions
{
    private const string ApplicationName = "DroneDB-Registry";
    private const string RedisKeyName = "DataProtection-Keys";

    /// <summary>
    /// Registers a shared Data Protection key ring. Two storage modes: Redis when the cache provider
    /// is Redis, otherwise a shared filesystem path (<see cref="StorageOptions.DataProtectionKeysPath"/>).
    /// Without either, each process keeps its keys where ASP.NET Core puts them by default (the user's
    /// profile folder, in a container its home folder): enough for a single process, but a deployment with
    /// separate processing nodes MUST set Redis or a shared folder, otherwise a node cannot decrypt what
    /// the web host encrypted.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="caching">The cache provider (Redis shares the key ring).</param>
    /// <param name="storage">The storage folders (the key ring folder otherwise).</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSharedDataProtection(this IServiceCollection services, CachingOptions caching,
        StorageOptions storage)
    {
        var dp = services.AddDataProtection().SetApplicationName(ApplicationName);

        if (caching.CacheProvider?.Type == CacheType.Redis)
        {
            // Reuse the shared IConnectionMultiplexer singleton already registered by AddCacheProvider,
            // instead of opening a second connection. The Configure<> callback runs lazily at first key access.
            dp.Services.AddOptions<KeyManagementOptions>()
                .Configure<IConnectionMultiplexer>((opts, mux) =>
                    opts.XmlRepository = new RedisXmlRepository(() => mux.GetDatabase(), RedisKeyName));
        }
        else if (!string.IsNullOrWhiteSpace(storage.DataProtectionKeysPath))
        {
            dp.PersistKeysToFileSystem(new DirectoryInfo(storage.DataProtectionKeysPath));
        }
        // else: the default key folder of this process (a single process with its in-process worker only)

        return services;
    }
}
