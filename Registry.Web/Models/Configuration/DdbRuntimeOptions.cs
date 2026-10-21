#nullable enable
using System.ComponentModel.DataAnnotations;
using Registry.Ports.DroneDB;

namespace Registry.Web.Models.Configuration;

/// <summary>
/// The libddb process configuration (configuration section <c>Ddb</c>), handed to libddb with
/// <c>DDBConfigure</c> before <c>DDBRegisterProcess</c> (XI-06, RQ-08). A value left out is not sent:
/// libddb uses its environment variable (<c>DDB_CPU_POOL_SIZE</c>, <c>DDB_IO_THREADS</c>,
/// <c>DDB_MIN_FREE_DISK_MB</c>) or its default. The effective values are logged at startup.
/// <para>
/// An explicit value here always wins. Left out, <see cref="CpuPoolSize"/> and <see cref="IoThreads"/>
/// come from the host budget when one is declared (<see cref="HostBudgetSettings"/>, XI-06; computed by
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
    /// Where libddb caches thumbnails, tiles, index templates and DSM tiles. Default:
    /// <c>&lt;StorageOptions.TempPath&gt;/ddb-cache</c>, absolute (before RQ-08 it was the profile folder,
    /// <c>~/.ddb</c>, inside the container layer).
    /// </summary>
    public string? CacheDir { get; set; }
}
