using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Utilities;
using static Registry.Web.Services.Configuration.ConfigurationFields;

namespace Registry.Web.Services.Configuration;

/// <summary>The admin configuration sections of where Registry keeps its data: database and cache providers, storage paths, cache expirations, Redis.</summary>
public sealed class DataStoreSections : IConfigurationSectionBuilder
{
    private readonly ConfigurationView _view;

    public DataStoreSections(ConfigurationView view)
    {
        _view = view;
    }

    public IEnumerable<PlacedSection> Build() =>
    [
        new(ConfigurationSection.Providers, BuildProvidersSection()),
        new(ConfigurationSection.StoragePaths, BuildStoragePathsSection()),
        new(ConfigurationSection.Cache, BuildCacheSection()),
        new(ConfigurationSection.Redis, BuildRedisSection())
    ];

    private ConfigurationSectionDataDto BuildProvidersSection()
    {
        var caching = _view.Configured<CachingOptions>();
        var cachingDefault = _view.Shipped<CachingOptions>();
        var store = _view.Configured<DataStoreOptions>();
        var storeDefault = _view.Shipped<DataStoreOptions>();
        var cacheType = caching.CacheProvider?.Type.ToString() ?? "InMemory";
        var defaultCacheType = cachingDefault.CacheProvider?.Type.ToString() ?? "InMemory";

        return Section("Providers", "Database & Cache Providers",
            "Backend providers for the registry database, Hangfire jobs, and caching.",
            [
                EnumField("RegistryProvider", "Registry Database Provider",
                    "Database provider for the main Registry database (datasets, organizations, etc.).",
                    store.RegistryProvider.ToString(), storeDefault.RegistryProvider.ToString(),
                    ["Sqlite", "Mysql"]),

                StringField("MySqlServerVersion", "MySQL Server Version",
                    "Pinned MySQL/MariaDB server version (e.g. 8.0.36-mysql, 10.11.6-mariadb). Empty: auto-detected once per connection string on first use.",
                    store.MySqlServerVersion, storeDefault.MySqlServerVersion),

                EnumField("HangfireProvider", "Hangfire Provider",
                    "Storage backend for Hangfire background jobs.",
                    store.HangfireProvider.ToString(), storeDefault.HangfireProvider.ToString(),
                    ["InMemory", "Mysql"]),

                EnumField("CacheProvider.Type", "Cache Provider",
                    "Caching backend for thumbnails, tiles, and dataset visibility. Null means InMemory.",
                    cacheType, defaultCacheType,
                    ["InMemory", "Redis"])
            ]);
    }

    private ConfigurationSectionDataDto BuildStoragePathsSection()
    {
        var storage = _view.Configured<StorageOptions>();
        var storageDefault = _view.Shipped<StorageOptions>();
        return Section("StoragePaths", "Storage Paths",
            "Filesystem paths for Registry data, datasets, cache, and temporary files.",
            [
                StringField("StoragePath", "Storage Path",
                    "Root storage path for Registry data (identity DB, config, etc.).",
                    storage.StoragePath, storageDefault.StoragePath),

                StringField("DatasetsPath", "Datasets Path",
                    "Directory where dataset archives and extracted content are stored.",
                    storage.DatasetsPath, storageDefault.DatasetsPath),

                StringField("CachePath", "Cache Path",
                    "Directory for file-based cache (thumbnails, tiles, etc.).",
                    storage.CachePath, storageDefault.CachePath),

                StringField("TempPath", "Temp Path",
                    "Directory for temporary files during processing.",
                    StorageOptions.ResolveTempPath(storage.TempPath), storageDefault.TempPath)
            ]);
    }

    private ConfigurationSectionDataDto BuildCacheSection()
    {
        var caching = _view.Configured<CachingOptions>();
        var cachingDefault = _view.Shipped<CachingOptions>();
        return Section("Cache", "Cache Expiration",
            "TTL for cached thumbnails, tiles, dataset visibility, authorization facts, and cache cleanup interval.",
            [
                TimeSpanField("ThumbnailsCacheExpiration", "Thumbnails Cache Expiration",
                    "How long thumbnail images are cached before regeneration.",
                    caching.ThumbnailsCacheExpiration, cachingDefault.ThumbnailsCacheExpiration),

                TimeSpanField("TilesCacheExpiration", "Tiles Cache Expiration",
                    "How long map tiles are cached before regeneration.",
                    caching.TilesCacheExpiration, cachingDefault.TilesCacheExpiration),

                TimeSpanField("DatasetVisibilityCacheExpiration", "Dataset Visibility Cache Expiration",
                    "How long dataset visibility settings are cached.",
                    caching.DatasetVisibilityCacheExpiration, cachingDefault.DatasetVisibilityCacheExpiration),

                TimeSpanField("AuthorizationCacheExpiration", "Authorization Cache Expiration",
                    "How long a user's roles and existence are reused across requests for access checks.",
                    caching.AuthorizationCacheExpiration, cachingDefault.AuthorizationCacheExpiration),

                TimeSpanField("DatasetLookupCacheExpiration", "Dataset Lookup Cache Expiration",
                    "How long a dataset lookup by slug is reused across requests. Changes made on this instance apply at once.",
                    caching.DatasetLookupCacheExpiration, cachingDefault.DatasetLookupCacheExpiration),

                TimeSpanField("ClearCacheInterval", "Clear Cache Interval",
                    "How often the file cache is scanned and stale entries are removed.",
                    caching.ClearCacheInterval, cachingDefault.ClearCacheInterval)
            ]);
    }

    // Only active (fields populated) when CacheProvider.Type is Redis
    private ConfigurationSectionDataDto BuildRedisSection()
    {
        var caching = _view.Configured<CachingOptions>();
        var cachingDefault = _view.Shipped<CachingOptions>();
        var cacheType = caching.CacheProvider?.Type.ToString() ?? "InMemory";
        if (cacheType != "Redis")
        {
            var inactive = Section("RedisConfig", "Redis Configuration",
                "Redis connection and cache settings. Only applied when Cache Provider is set to Redis. Current provider: " + cacheType + ".",
                []);
            inactive.IsActive = false;
            return inactive;
        }

        var redisSettings = caching.CacheProvider?.Settings?.ToObject<RedisProviderSettings>();
        var redisDefaults = cachingDefault.CacheProvider?.Settings?.ToObject<RedisProviderSettings>();

        return Section("RedisConfig", "Redis Configuration",
            "Redis cache connection address, key prefix, and default TTL. Only used when Cache Provider is Redis.",
            [
                StringField("CacheProvider.Settings.InstanceAddress", "Redis Instance Address",
                    "Connection address for Redis (e.g., 'localhost:6379').",
                    redisSettings?.InstanceAddress, redisDefaults?.InstanceAddress),

                StringField("CacheProvider.Settings.InstanceName", "Redis Key Prefix",
                    "Instance name used as a prefix for all cache keys, enabling multi-instance isolation on a shared Redis server.",
                    redisSettings?.InstanceName, redisDefaults?.InstanceName),

                TimeSpanField("CacheProvider.Settings.Expiration", "Redis Default Expiration",
                    "Default TTL for cache entries stored in Redis, if not specified by the caller.",
                    redisSettings?.Expiration, redisDefaults?.Expiration)
            ]);
    }
}
