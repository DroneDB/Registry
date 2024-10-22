namespace Registry.Web.Models.Configuration;

public class RedisProviderSettings : CacheProviderSettings
{
    public string InstanceAddress { get; set; }
    public string InstanceName { get; set; }
}