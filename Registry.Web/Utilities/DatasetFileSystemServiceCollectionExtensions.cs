#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Registry.Ports;

namespace Registry.Web.Utilities;

/// <summary>
/// The file system services of every host that touches dataset folders (RQ-10): the web server and
/// the processing node register the same set, so a heavy tool resolves on both.
/// </summary>
public static class DatasetFileSystemServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DatasetPathPolicy"/> (libddb's dataset path rule, through
    /// <see cref="IDdbWrapper"/>). The host registers the adapters (<see cref="IFileSystem"/>,
    /// <see cref="IDdbWrapper"/>).
    /// </summary>
    public static IServiceCollection AddDatasetFileSystem(this IServiceCollection services)
    {
        services.TryAddSingleton<DatasetPathPolicy>();
        return services;
    }
}
