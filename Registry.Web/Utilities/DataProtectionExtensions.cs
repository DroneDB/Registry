#nullable enable
using System;
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
/// node can encrypt/decrypt the same protected payloads (import credentials). See the Import Dataset
/// plan section 2.4 for the storage-choice rationale.
/// </summary>
public static class DataProtectionExtensions
{
    private const string ApplicationName = "DroneDB-Registry";
    private const string RedisKeyName = "DataProtection-Keys";

    /// <summary>
    /// Registers a shared Data Protection key ring. Two storage modes: Redis when the cache provider
    /// is Redis, otherwise a shared filesystem path (<see cref="AppSettings.DataProtectionKeysPath"/>).
    /// In-memory keys are an implicit local-dev fallback only - a multi-host deployment MUST set Redis
    /// or a filesystem path, otherwise the processing node cannot decrypt what the web host encrypted.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="settings">The application settings.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSharedDataProtection(this IServiceCollection services, AppSettings settings)
    {
        var dp = services.AddDataProtection().SetApplicationName(ApplicationName);

        if (settings.CacheProvider?.Type == CacheType.Redis)
        {
            // Reuse the shared IConnectionMultiplexer singleton already registered by AddCacheProvider,
            // instead of opening a second connection. The Configure<> callback runs lazily at first key access.
            dp.Services.AddOptions<KeyManagementOptions>()
                .Configure<IConnectionMultiplexer>((opts, mux) =>
                    opts.XmlRepository = new RedisXmlRepository(() => mux.GetDatabase(), RedisKeyName));
        }
        else if (!string.IsNullOrWhiteSpace(settings.DataProtectionKeysPath))
        {
            dp.PersistKeysToFileSystem(new DirectoryInfo(settings.DataProtectionKeysPath));
        }
        // else: in-memory keys (LOCAL DEV ONLY - single process with in-process worker).

        return services;
    }
}
