#nullable enable
using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Registry.Web.Hosting;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Ports;
using Registry.Web.Services.UpdateCheck;

namespace Registry.Web.Utilities;

/// <summary>
/// The update check, for every host that runs jobs: the job runs on whichever node processes the default queue. Part of the
/// shared job services (<see cref="ProcessingNodeServiceCollectionExtensions"/>); the web host adds its own part with
/// <c>AddUpdateCheckWeb</c>.
/// </summary>
public static class UpdateCheckServiceCollectionExtensions
{
    /// <summary>
    /// The instance identity (created at the start), the update check service and the files it keeps in the storage folder,
    /// and the HTTP client it posts with.
    /// </summary>
    public static IServiceCollection AddUpdateCheck(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IInstanceIdentity, InstanceIdentityStore>();
        services.AddStartupTask<InstanceIdInitializer>();

        services.AddSingleton<UpdateCheckSettingsStore>();
        services.AddSingleton<ObservedBaseUrlStore>();
        services.AddSingleton<IUpdateCheckCache, UpdateCheckCacheStore>();
        services.AddSingleton<IPlatformFacts, PlatformFacts>();
        services.AddSingleton<InstanceBaseUrl>();

        services.AddScoped<UpdateCheckPayloadBuilder>();
        services.AddScoped<UpdateInfoBuilder>();
        services.AddScoped<UpdateCheckClient>();
        services.AddScoped<UpdateCheckExchange>();
        services.AddScoped<IUpdateCheckService, UpdateCheckService>();

        // The e-mail to the administrators about a new version. The recipients come from the identity database, which the
        // shared job services register for every host that runs jobs (AddProcessingNodeServices)
        services.AddScoped<IAdminRecipients, IdentityAdminRecipients>();
        services.AddScoped<IUpdateNotifier, UpdateNotifier>();

        // Its own client, not the SSRF-guarded one of the imports: the endpoint is chosen by the administrator (a staging
        // service may be on a private network); no redirect is followed, and the exchange is bounded
        services.AddHttpClient(UpdateCheckClient.HttpClientName, client =>
            {
                client.Timeout = UpdateCheckClient.Timeout;
                if (RegistryVersion.Current is { } version)
                    client.DefaultRequestHeaders.UserAgent.ParseAdd($"DroneDB-Registry/{version}");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        return services;
    }
}
