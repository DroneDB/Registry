using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Data;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Handles dataset visibility cache preloading during application startup.
/// Loads all dataset visibilities into cache to avoid filesystem reads on first access.
/// </summary>
internal class DatasetVisibilityCacheInitializer
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    // Thread-safe counters for parallel operations
    private int _successCount;
    private int _failCount;

    public DatasetVisibilityCacheInitializer(IServiceProvider services, ILogger logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PreloadAsync(CancellationToken token)
    {
        _logger.LogInformation("Dataset visibility cache preload starting");

        try
        {
            var context = _services.GetRequiredService<RegistryContext>();
            var ddbManager = _services.GetRequiredService<IDdbManager>();
            var cacheManager = _services.GetRequiredService<ICacheManager>();

            // Query all datasets (lightweight: only ID, slug, ref)
            var datasets = await context.Datasets
                .Include(ds => ds.Organization)
                .Select(ds => new
                {
                    ds.InternalRef,
                    OrgSlug = ds.Organization.Slug,
                    DsSlug = ds.Slug
                })
                .ToListAsync(token);

            _logger.LogInformation("Preloading visibility for {Count} datasets", datasets.Count);

            // Reset counters
            _successCount = 0;
            _failCount = 0;

            // Throttled parallel loading (max cores concurrent to avoid overwhelming disk)
            await Parallel.ForEachAsync(datasets,
                new ParallelOptions
                {
                    // Use all available cores
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = token
                },
                async (ds, ct) =>
                {
                    await PreloadDatasetVisibilityAsync(ds.OrgSlug, ds.DsSlug, ds.InternalRef,
                        ddbManager, cacheManager, datasets.Count);
                });

            _logger.LogInformation(
                "Dataset visibility cache preload completed: {Success} success, {Fail} failed, {Total} total",
                _successCount, _failCount, datasets.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Dataset visibility cache preload was canceled");
            // Don't rethrow - allow server to continue starting
        }
        catch (Exception ex)
        {
            // Log the error but don't crash the server - cache preload is non-critical
            _logger.LogError(ex, "Dataset visibility cache preload failed with exception. Server will continue without preloaded cache.");
        }
    }

    /// <summary>
    /// Preloads visibility for a single dataset with comprehensive exception handling.
    /// Native exceptions from DroneDB are caught and logged without crashing the server.
    /// </summary>
    private async Task PreloadDatasetVisibilityAsync(
        string orgSlug,
        string dsSlug,
        Guid internalRef,
        IDdbManager ddbManager,
        ICacheManager cacheManager,
        int totalCount)
    {
        try
        {
            // This will cache if miss (calls GetAsync which triggers provider)
            await cacheManager.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                orgSlug,
                orgSlug,
                internalRef,
                ddbManager
            );

            var count = Interlocked.Increment(ref _successCount);

            if (count % 100 == 0)
            {
                _logger.LogDebug("Preloaded {Count}/{Total} dataset visibilities",
                    count, totalCount);
            }
        }
        catch (SEHException sehEx)
        {
            // Native structured exception (e.g., access violation in native DroneDB code)
            _logger.LogError(sehEx,
                "Native exception (SEH) while preloading visibility for {Org}/{Ds} ({Ref}). Error code: 0x{ErrorCode:X8}",
                orgSlug, dsSlug, internalRef, sehEx.ErrorCode);
            Interlocked.Increment(ref _failCount);
        }
        catch (ExternalException extEx)
        {
            // Other native/COM exceptions
            _logger.LogError(extEx,
                "External exception while preloading visibility for {Org}/{Ds} ({Ref}). Error code: 0x{ErrorCode:X8}",
                orgSlug, dsSlug, internalRef, extEx.ErrorCode);
            Interlocked.Increment(ref _failCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to preload visibility for {Org}/{Ds} ({Ref})",
                orgSlug, dsSlug, internalRef);
            Interlocked.Increment(ref _failCount);
        }
    }
}
