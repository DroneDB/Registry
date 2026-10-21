#nullable enable
using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Utilities;

/// <summary>The libddb process lifecycle of a host (RQ-08): one registration for the web server, the processing node and the thumbnail generator.</summary>
public static class DdbRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TRuntime"/> as the <see cref="IDdbRuntime"/> (the composition root
    /// passes the native one), the native log sink (XI-08), the shutdown budget
    /// (<see cref="ShutdownTimeouts.Host"/>) and <see cref="DdbRuntimeService"/>, which configures libddb
    /// with the host's <see cref="HostResourceBudget"/> (XI-06; <see cref="DdbRuntimeOptions"/> is
    /// registered with the other options by <c>AddRegistryOptions</c>). <c>RegistryHost</c> calls it before
    /// any other hosted service is added, so the runtime starts first and stops last (XI-07).
    /// </summary>
    public static IServiceCollection AddDdbRuntime<TRuntime>(this IServiceCollection services)
        where TRuntime : class, IDdbRuntime
    {
        services.TryAddSingleton<IDdbRuntime, TRuntime>();
        services.TryAddSingleton<DdbLogSink>();
        services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownTimeouts.Host);
        services.AddHostedService<DdbRuntimeService>();
        return services;
    }

    /// <summary>
    /// The absolute cache folder: the configured one, else <c>&lt;tempPath&gt;/ddb-cache</c>; null (libddb's
    /// default, the profile folder) when neither is set. Relative paths resolve against the current
    /// folder, the storage folder of a running Registry.
    /// </summary>
    public static string? ResolveCacheDir(string? configured, string? tempPath)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        return string.IsNullOrWhiteSpace(tempPath)
            ? null
            : Path.GetFullPath(Path.Combine(tempPath, DdbRuntimeOptions.DefaultCacheFolderName));
    }
}
