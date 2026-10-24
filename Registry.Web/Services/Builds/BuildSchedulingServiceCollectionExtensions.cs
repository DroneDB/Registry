#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Builds;

/// <summary>Registration of the build scheduling every job host shares.</summary>
public static class BuildSchedulingServiceCollectionExtensions
{
    /// <summary>
    /// libddb's build manifest by role (XI-04), the one place that queues builds (RQ-12) and the pending flag it
    /// sets; builds queued without an origin run as the service (the web host replaces the requester with the
    /// signed-in user). Needs the job services (<see cref="IBackgroundJobsProcessor"/>, <see cref="BuildPendingService"/>).
    /// </summary>
    public static IServiceCollection AddBuildScheduling(this IServiceCollection services)
    {
        services.AddSingleton<IBuildArtifactResolver, BuildArtifactResolver>();
        services.AddScoped<BuildScheduler>();
        services.AddScoped<IPendingBuildFlag, BuildPendingService>();
        services.AddScoped<IBuildRequester, ServiceBuildRequester>();
        return services;
    }
}
