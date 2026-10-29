using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Services.UpdateCheck;

namespace Registry.Web.Hosting;

/// <summary>The web host's part of the update check; the rest is shared with every host that runs jobs (<c>AddUpdateCheck</c>).</summary>
public static class UpdateCheckWebServiceCollectionExtensions
{
    /// <summary>What only the web host needs: saving the administrator's declarations, and remembering the public URL requests arrive on.</summary>
    public static IServiceCollection AddUpdateCheckWeb(this IServiceCollection services)
    {
        services.AddScoped<UpdateCheckSettingsUpdater>();
        services.AddTransient<Middlewares.ObservedBaseUrlMiddleware>();
        return services;
    }
}
