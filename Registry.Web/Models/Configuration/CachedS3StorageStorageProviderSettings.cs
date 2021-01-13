using System;

namespace Registry.Web.Models.Configuration
{
    public class CachedS3StorageStorageProviderSettings : S3StorageProviderSettings
    {
        public string CachePath { get; set; }
        public long MaxSize { get; set; }
        public TimeSpan CacheExpiration { get; set; }
    }
}