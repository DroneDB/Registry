#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities;

public static class JobIndexingServiceCollectionExtensions
{
    /// <summary>
    /// Registers DbContext/services. The global Hangfire filter is added
    /// during Hangfire configuration (see Examples.ConfigureServices).
    /// </summary>
    public static IServiceCollection AddJobIndexing(this IServiceCollection services)
    {
        services.AddScoped<IJobIndexWriter, JobIndexWriter>();
        services.AddScoped<IJobIndexQuery, JobIndexQuery>();
        services.AddSingleton<IIndexedJobEnqueuer, IndexedJobEnqueuer>();

        // The filter depends on the DI provider, we register it for injection
        services.AddScoped<JobIndexStateFilter>();
        return services;
    }
}