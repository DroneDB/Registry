using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Registry.Adapters;
using Registry.Adapters.DroneDB;
using Registry.Adapters.Thumbnail;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Hosting;

/// <summary>
/// Where the adapters of <c>Registry.Adapters</c> are bound to their ports: the composition root
/// registers them here and the rest of Registry.Web depends on <c>Registry.Ports</c> only (plus
/// <c>DdbManager</c>, which builds the dataset adapter it hands out).
/// </summary>
public static class AdapterServiceCollectionExtensions
{
    /// <summary>
    /// libddb's process lifecycle (configure, register the process, shut down) over the native library, started
    /// first and stopped last by every host.
    /// </summary>
    public static IServiceCollection AddNativeDdbRuntime(this IServiceCollection services) =>
        services.AddDdbRuntime<NativeDdbRuntime>();

    /// <summary>libddb itself: the native wrapper behind <see cref="IDdbWrapper"/>, one per process.</summary>
    public static IServiceCollection AddNativeDdbWrapper(this IServiceCollection services)
    {
        services.AddSingleton<IDdbWrapper, NativeDdbWrapper>();
        return services;
    }

    /// <summary>The file system behind <see cref="IFileSystem"/>.</summary>
    public static IServiceCollection AddFileSystemAdapter(this IServiceCollection services)
    {
        services.AddSingleton<IFileSystem, FileSystem>();
        return services;
    }

    /// <summary>
    /// The thumbnail generator behind <see cref="IThumbnailGenerator"/>, gated on the native Heavy lane: the remote
    /// thumbnail generator host when <see cref="ThumbnailOptions.RemoteThumbnailGeneratorUrl"/> is set, libddb
    /// in this process otherwise.
    /// </summary>
    public static IServiceCollection AddThumbnailGeneratorAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!string.IsNullOrWhiteSpace(configuration.GetRegistryOptions<ThumbnailOptions>().RemoteThumbnailGeneratorUrl))
        {
            services.AddOptions<RemoteThumbnailGeneratorSettings>().Configure<IOptions<ThumbnailOptions>>((options, thumbnails) =>
            {
                options.Url = thumbnails.Value.RemoteThumbnailGeneratorUrl!;
                options.Token = thumbnails.Value.ThumbnailGeneratorToken;
            });
            AddGatedThumbnailGenerator<RemoteThumbnailGenerator>(services);
        }
        else
            AddGatedThumbnailGenerator<LocalThumbnailGenerator>(services);

        return services;
    }

    // The generator behind the native work gate (GatedThumbnailGenerator), whichever renders. The inner one exists only
    // behind the gate, so it is built with it and is no service of its own: nothing can reach it around the gate.
    private static void AddGatedThumbnailGenerator<TGenerator>(IServiceCollection services)
        where TGenerator : class, IThumbnailGenerator =>
        services.AddSingleton<IThumbnailGenerator>(sp =>
            ActivatorUtilities.CreateInstance<GatedThumbnailGenerator>(sp, ActivatorUtilities.CreateInstance<TGenerator>(sp)));
}
