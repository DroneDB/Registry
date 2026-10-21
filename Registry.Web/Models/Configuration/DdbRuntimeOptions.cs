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
/// A minimal focused options class: RQ-07 splits <see cref="AppSettings"/> into classes like this
/// one and adds the host budget split by role (web, nodes) that sets <see cref="CpuPoolSize"/>.
/// </para>
/// </summary>
public sealed class DdbRuntimeOptions
{
    public const string SectionName = "Ddb";

    /// <summary>Name of the folder under <see cref="AppSettings.TempPath"/> that is the default <see cref="CacheDir"/>.</summary>
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
    /// <c>&lt;AppSettings.TempPath&gt;/ddb-cache</c>, absolute (before RQ-08 it was the profile folder,
    /// <c>~/.ddb</c>, inside the container layer).
    /// </summary>
    public string? CacheDir { get; set; }

    /// <summary>The <c>DDBConfigure</c> settings.</summary>
    public DdbProcessSettings ToProcessSettings() => new(CpuPoolSize, IoThreads, MinFreeDiskMb, CacheDir);
}
