#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities;

/// <summary>
/// The file system services of every host that touches dataset folders (RQ-10): the web server and
/// the processing node register the same set, so a heavy tool resolves on both.
/// </summary>
public static class DatasetFileSystemServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DatasetPathPolicy"/> (libddb's dataset path rule, through
    /// <see cref="IDdbWrapper"/>) and <see cref="IScratchFileProvider"/> (<see cref="ScratchOptions"/>).
    /// The host registers the adapters (<see cref="IFileSystem"/>, <see cref="IDdbWrapper"/>).
    /// </summary>
    public static IServiceCollection AddDatasetFileSystem(this IServiceCollection services)
    {
        services.TryAddSingleton<DatasetPathPolicy>();

        // The temp folder is AppSettings:TempPath: the one binding to change when the options are split (RQ-07)
        services.AddOptions<ScratchOptions>()
            .Configure<IOptions<AppSettings>>((scratch, app) => scratch.TempPath = app.Value.TempPath);
        services.TryAddSingleton<IScratchFileProvider, ScratchFileProvider>();
        return services;
    }
}
