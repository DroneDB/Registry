using System;

namespace Registry.Web.Models.Configuration
{
    public abstract class CacheProviderSettings
    {
        public TimeSpan Expiration { get; set; }
    }
}