using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.Configuration;

/// <summary>
/// The libddb process configuration (configuration section <c>Ddb</c>), handed to libddb with
/// <c>DDBConfigure</c> before <c>DDBRegisterProcess</c>. A value left out is not sent:
/// libddb uses its environment variable (<c>DDB_CPU_POOL_SIZE</c>, <c>DDB_IO_THREADS</c>,
/// <c>DDB_MIN_FREE_DISK_MB</c>, <c>DDB_GDAL_CACHE_MB</c>) or its default. The effective values are logged at startup.
/// <para>
/// An explicit value here always wins. Left out, <see cref="CpuPoolSize"/> and <see cref="IoThreads"/>
/// come from the host budget when one is declared (<see cref="HostBudgetSettings"/>; computed by
/// <c>Utilities.HostResourceBudget</c>), unless the libddb environment variable is set.
/// </para>
/// </summary>
public sealed class DdbRuntimeOptions
{
    public const string SectionName = "Ddb";

    /// <summary>Name of the folder under <see cref="StorageOptions.TempPath"/> that is the default <see cref="CacheDir"/>.</summary>
    public const string DefaultCacheFolderName = "ddb-cache";

    /// <summary>Worker threads libddb may run at once beside the calling threads (libddb default: cores - 1).</summary>
    [Range(0, 4096)]
    public int? CpuPoolSize { get; set; }

    /// <summary>Threads of one disk-bound section: hashing, deleting build folders (libddb default: 4).</summary>
    [Range(1, 256)]
    public int? IoThreads { get; set; }

    /// <summary>Free MiB a build needs on the volume it writes to; 0 turns the check off (libddb default: 512).</summary>
    [Range(0L, 10_000_000_000_000L)]
    public long? MinFreeDiskMb { get; set; }

    /// <summary>
    /// Size in MiB of GDAL's block cache, which the whole process shares (tiles, renders, thumbnails, statistics,
    /// builds). Sent with <c>DDBConfigure</c>: it is the size of the cache. Left out, libddb keeps its default
    /// (1024, or <c>DDB_GDAL_CACHE_MB</c>, an upper bound only). Several processes on one host share its memory:
    /// size this for the role.
    /// </summary>
    [Range(16, 1_048_576)]
    public int? GdalCacheMb { get; set; }

    /// <summary>
    /// Where libddb caches thumbnails, tiles, index templates and DSM tiles. Default:
    /// <c>&lt;StorageOptions.TempPath&gt;/ddb-cache</c>, absolute (not the profile folder
    /// <c>~/.ddb</c>, which lives in the container layer).
    /// </summary>
    public string? CacheDir { get; set; }
}
