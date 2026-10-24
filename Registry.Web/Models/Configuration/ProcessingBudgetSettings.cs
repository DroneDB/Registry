namespace Registry.Web.Models.Configuration;

/// <summary>
/// How much of the host background jobs may use. Bound from <c>AppSettings:ProcessingBudget</c>.
/// Every value is optional: 0 (or unset) derives it from the host's cores and memory, see
/// <see cref="Utilities.ProcessingBudget"/>.
/// </summary>
public class ProcessingBudgetSettings
{
    /// <summary>Concurrent jobs that run native builds (COG, COPC, Nexus, LOD, raster tools).</summary>
    public int HeavyWorkers { get; set; }

    /// <summary>Concurrent other jobs (imports, exports, cleanup, thumbnails, scheduling).</summary>
    public int LightWorkers { get; set; }

    /// <summary>Threads one native build may use (DDB_BUILD_THREADS).</summary>
    public int BuildThreads { get; set; }

    /// <summary>GDAL block cache during a raster build, in MB (DDB_COG_CACHE_MB).</summary>
    public int BuildCacheMb { get; set; }

    /// <summary>GDAL warp working memory during a raster build, in MB (DDB_WARP_MEMORY_MB).</summary>
    public int BuildWarpMemoryMb { get; set; }
}
