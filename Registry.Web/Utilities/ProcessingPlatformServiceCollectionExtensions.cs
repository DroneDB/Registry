using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Hosting;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.NodeOdx;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities;

/// <summary>
/// DI registration for the Processing Platform task substrate (Layer 1).
/// <see cref="AddProcessingPlatform"/> registers what executes tasks (native tool catalog,
/// in-memory registry, system task submitter, Hangfire job wrapper) for every job-executing
/// host; <see cref="AddProcessingPlatformSubmission"/> adds the caller-facing submission side
/// (gating, quota guard, runner) for the web host only.
/// </summary>
public static class ProcessingPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the task execution substrate. Call this from both the web host and the
    /// processing-node host so native tools (e.g. <c>build</c>) can execute.
    /// </summary>
    public static IServiceCollection AddProcessingPlatform(this IServiceCollection services)
    {
        // ZIP archive builder shared by the legacy download streaming path and the
        // bulk-download heavy tool. Must be registered here so both the web host and
        // the processing-node host get the dependency (BulkDownloadTool ctor requires it).
        services.AddSingleton<IZipArchiveBuilder, ZipArchiveBuilder>();

        // NodeODX (OpenDroneMap) integration: config-based node registry + HTTP client.
        services.AddSingleton<NodeOdxNodeRegistry>();
        services.AddSingleton<INodeOdxClient, NodeOdxClient>();
        services.AddSingleton<NodeOdxTaskRunner>();

        // Archive extraction backend (SharpCompress) for the archive-extract tool.
        services.AddSingleton<Ports.Archives.IArchiveExtractor,
            Adapters.Archives.SharpCompressArchiveExtractor>();

        // Focused dataset cache invalidation (extracted from IObjectsManager) so heavy tools
        // can clear dataset caches on hosts without the full object-management stack.
        services.AddSingleton<IDatasetCacheInvalidator, DatasetCacheInvalidator>();

        // What a task does with the files it wrote: index, queue their builds, invalidate the caches
        services.AddSingleton<TaskFileIndexer>();

        // What a rescan rebuilds, shared by the rescan-index tool and the rescan endpoint.
        services.AddScoped<RescanRebuild>();

        // The export size limit of the raster-export submit: the check of the direct export (FR-RAS-16, D-023)
        services.AddSingleton<IRasterExportSizeCheck, Services.Objects.RasterExportSizeCheck>();

        // Native tools (Sprint 1). Adding a new native tool = one line here.
        services.AddSingleton<IHeavyTool, BuildTool>();
        services.AddSingleton<IHeavyTool, RasterExportTool>();
        services.AddSingleton<IHeavyTool, RasterContoursTool>();
        services.AddSingleton<IHeavyTool, PhotogrammetryTool>();
        services.AddSingleton<IHeavyTool, BulkDownloadTool>();
        services.AddSingleton<IHeavyTool, MergeMultispectralTool>();
        services.AddSingleton<IHeavyTool, AlignRasterTool>();
        services.AddSingleton<IHeavyTool, MaskBordersTool>();
        services.AddSingleton<IHeavyTool, RescanIndexTool>();
        services.AddSingleton<IHeavyTool, ArchiveExtractTool>();
        services.AddSingleton<IHeavyTool, SplatTrainingExportTool>();
        services.AddSingleton<IHeavyTool, SplatSpzExportTool>();

        // Catalog is immutable for the process lifetime.
        services.AddSingleton<IHeavyToolRegistry, HeavyToolRegistry>();

        // System-initiated submissions (no caller), e.g. the rescan job on the default queue.
        services.AddScoped<ISystemTaskSubmitter, SystemTaskSubmitter>();

        // The work folder of a task that produces an artifact
        services.AddSingleton<TaskWorkDirectories>();

        // Hangfire activates the wrapper from DI on the worker host.
        services.AddScoped<HeavyTaskJobWrapper>();

        // The write record of a task and its one rollback (FR-XC-27, D-045), wherever a task runs or is cancelled: the
        // delayed system delete of a dataset a rolled-back task created, the removal it shares with the user delete,
        // and the startup sweep that finishes the rollbacks a stop interrupted (before the job servers start)
        services.AddScoped<ITaskWriteRecordStore, TaskWriteRecordStore>();
        services.AddScoped<ITaskRollback, TaskRollback>();
        services.AddScoped<TaskEntryChanges>();
        services.AddScoped<CreatedDatasetRollbackJob>();
        services.AddScoped<DatasetRemoval>();
        // The system creation of the dataset a task makes for its results (photogrammetry, D-031)
        services.AddScoped<DatasetCreation>();
        services.AddScoped<IDatasetListingInvalidator, DatasetListingInvalidator>();
        services.AddStartupTask<TaskRollbackStartupTask>();

        return services;
    }

    /// <summary>
    /// Registers the caller-facing task submission services. Web host only: they depend on the
    /// request user (<see cref="IAuthManager"/>), which the processing node
    /// does not register, so the node container must not hold them (the DI validation tests check it).
    /// </summary>
    public static IServiceCollection AddProcessingPlatformSubmission(this IServiceCollection services)
    {
        // Feature gating: Scoped because it resolves the current caller via IAuthManager.
        services.AddScoped<IHeavyToolGating, HeavyToolGating>();

        // Per-request orchestration.
        services.AddScoped<HeavyTaskQuota>();
        services.AddScoped<IHeavyTaskRunner, HeavyTaskRunner>();
        services.AddScoped<HeavyTaskDedup>();
        services.AddScoped<HeavyTaskSubmitLock>();
        services.AddScoped<CurrentUserTaskSubmitter>();

        return services;
    }
}
