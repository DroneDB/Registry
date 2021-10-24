using System;
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.Configuration
{
    public class CachedS3StorageStorageProviderSettings : S3StorageProviderSettings
    {
        [Required]
        public string CachePath { get; set; }
        public long? MaxSize { get; set; }
        public TimeSpan? CacheExpiration { get; set; }
    }
}