#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.NodeOdm;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.HeavyTasks.Tools;

namespace Registry.Web.Utilities;

/// <summary>
/// DI registration for the Processing Platform task substrate (Layer 1).
/// Registers the native tool catalog, the in-memory registry, the quota guard,
/// the runner and the Hangfire job wrapper. Call this from both the web host and
/// the processing-node host so native tools (e.g. <c>build</c>) can execute.
/// </summary>
public static class ProcessingPlatformServiceCollectionExtensions
{
    public static IServiceCollection AddProcessingPlatform(this IServiceCollection services)
    {
        // NodeODM (OpenDroneMap) integration: config-based node registry + HTTP client.
        services.AddSingleton<INodeOdmNodeRegistry, NodeOdmNodeRegistry>();
        services.AddSingleton<INodeOdmClient, NodeOdmClient>();

        // Archive extraction backend (SharpCompress) for the archive-extract tool.
        services.AddSingleton<Registry.Ports.Archives.IArchiveExtractor,
            Registry.Adapters.Archives.SharpCompressArchiveExtractor>();

        // Native tools (Sprint 1). Adding a new native tool = one line here.
        services.AddSingleton<IHeavyTool, BuildTool>();
        services.AddSingleton<IHeavyTool, RasterExportTool>();
        services.AddSingleton<IHeavyTool, PhotogrammetryTool>();
        services.AddSingleton<IHeavyTool, BulkDownloadTool>();
        services.AddSingleton<IHeavyTool, MergeMultispectralTool>();
        services.AddSingleton<IHeavyTool, RescanIndexTool>();
        services.AddSingleton<IHeavyTool, ArchiveExtractTool>();

        // Catalog is immutable for the process lifetime.
        services.AddSingleton<IHeavyToolRegistry, HeavyToolRegistry>();

        // Per-request orchestration.
        services.AddScoped<IHeavyTaskQuota, HeavyTaskQuota>();
        services.AddScoped<IHeavyTaskRunner, HeavyTaskRunner>();

        // Hangfire activates the wrapper from DI on the worker host.
        services.AddScoped<HeavyTaskJobWrapper>();

        return services;
    }
}
