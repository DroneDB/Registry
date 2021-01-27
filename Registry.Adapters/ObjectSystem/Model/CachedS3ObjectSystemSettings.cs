using System;

namespace Registry.Adapters.ObjectSystem.Model
{
    public class CachedS3ObjectSystemSettings : S3ObjectSystemSettings
    {
        public string CachePath { get; set; }
        public TimeSpan? CacheExpiration { get; set; }
        public long? MaxSize { get; set; }
    }
}