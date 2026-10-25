#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Utilities;

/// <summary>
/// The file system services of every host that touches dataset folders: the web server and
/// the processing node register the same set, so a heavy tool resolves on both.
/// </summary>
public static class DatasetFileSystemServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DatasetPathPolicy"/> (libddb's dataset path rule, through
    /// <see cref="IDdbWrapper"/>), <see cref="ScratchFileProvider"/> (<see cref="StorageOptions"/>) and
    /// <see cref="VolumeHeadroom"/> (over <see cref="Registry.Ports.DroneDB.IDdbRuntime"/>) and
    /// <see cref="DatasetArchiveExtractor"/>.
    /// The host registers the adapters (<see cref="IFileSystem"/>, <see cref="IDdbWrapper"/>).
    /// </summary>
    public static IServiceCollection AddDatasetFileSystem(this IServiceCollection services)
    {
        services.TryAddSingleton<DatasetPathPolicy>();

        // The temp folder: StorageOptions.TempPath (AppSettings:TempPath, bound by RegistryOptions)
        services.TryAddSingleton<ScratchFileProvider>();

        // The disk-space API: the path's own volume plus libddb's free-disk floor (IDdbRuntime)
        services.TryAddSingleton<VolumeHeadroom>();

        // The one archive extraction into a dataset (the host registers IArchiveExtractor)
        services.TryAddSingleton<DatasetArchiveExtractor>();

        // The clock of the managers that store times (share batches, their cleanup)
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
