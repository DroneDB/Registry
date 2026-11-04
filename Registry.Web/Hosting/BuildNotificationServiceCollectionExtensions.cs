using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Ports;

namespace Registry.Web.Hosting;

/// <summary>
/// The build notification e-mails, for every host that runs jobs: the state filter that sees a build end runs on the host
/// that applies the state, and the digest runs on whichever host takes the job, so the web host and the processing nodes
/// register the same services (the e-mail itself, <see cref="EmailServiceCollectionExtensions.AddEmail"/>, too).
/// </summary>
public static class BuildNotificationServiceCollectionExtensions
{
    public static IServiceCollection AddBuildNotifications(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IBuildOutcomeClassifier, BuildOutcomeClassifier>();
        services.AddScoped<IBuildRequesterResolver, BuildRequesterResolver>();
        // From the identity database every host that runs jobs shares (no UserManager needed)
        services.AddScoped<IBuildNotificationLevels, IdentityBuildNotificationLevels>();
        // Which datasets a user may still read when the digest is sent, by the API's rules (no request needed)
        services.AddScoped<IUserDatasetAccess, IdentityUserDatasetAccess>();
        services.AddScoped<BuildNotificationAudience>();
        services.AddScoped<BuildNotificationOutbox>();
        services.AddScoped<BuildNotificationDigests>();
        services.AddScoped<IBuildNotificationRecorder, BuildNotificationRecorder>();
        services.AddScoped<FinishedBuildReporter>();
        services.AddScoped<BuildNotificationCleanup>();

        // The job class, registered like the others so a missing dependency fails the container validation
        services.AddScoped<BuildNotificationDigestJob>();

        return services;
    }
}
