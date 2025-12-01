using System;
using System.Linq;
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

            var successCount = 0;
            var failCount = 0;

            // Throttled parallel loading (max 20 concurrent to avoid overwhelming disk)
            await Parallel.ForEachAsync(datasets,
                new ParallelOptions
                {
                    // Use all available cores
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = token
                },
                async (ds, ct) =>
                {
                    try
                    {
                        // This will cache if miss (calls GetAsync which triggers provider)
                        await cacheManager.GetAsync(
                            MagicStrings.DatasetVisibilityCacheSeed,
                            ds.OrgSlug,
                            ds.OrgSlug,
                            ds.InternalRef,
                            ddbManager
                        );

                        Interlocked.Increment(ref successCount);

                        if (successCount % 100 == 0)
                        {
                            _logger.LogDebug("Preloaded {Count}/{Total} dataset visibilities",
                                successCount, datasets.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to preload visibility for {Org}/{Ds} ({Ref})",
                            ds.OrgSlug, ds.DsSlug, ds.InternalRef);
                        Interlocked.Increment(ref failCount);
                    }
                });

            _logger.LogInformation(
                "Dataset visibility cache preload completed: {Success} success, {Fail} failed, {Total} total",
                successCount, failCount, datasets.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dataset visibility cache preload failed with exception");
            throw;
        }
    }
}
