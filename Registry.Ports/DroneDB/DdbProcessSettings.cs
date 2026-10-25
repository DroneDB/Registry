namespace Registry.Ports.DroneDB;

/// <summary>
/// The process configuration a host gives libddb before <c>DDBRegisterProcess</c>
/// (<c>DDBConfigure</c>, ABI 2.0). A null value is not sent: libddb then uses its
/// environment variable or its default.
/// </summary>
/// <param name="CpuPoolSize">Worker threads libddb may run at once beside the calling threads (0..4096).</param>
/// <param name="IoThreads">Threads of one disk-bound section: hashing, deleting build folders (1..256).</param>
/// <param name="MinFreeDiskMb">Free MiB a build needs on the volume it writes to; 0 turns the check off.</param>
/// <param name="CacheDir">Absolute folder where libddb caches thumbnails, tiles, index templates and DSM tiles.</param>
/// <param name="GdalCacheMb">Size in MiB of GDAL's block cache, shared by the whole process (16..1048576). Sent
/// with <c>DDBConfigure</c> it is the size of the cache at once (raising or lowering GDAL's own default);
/// left out libddb keeps its default (1024 MiB, an upper bound only: <c>DDB_GDAL_CACHE_MB</c>).</param>
public sealed record DdbProcessSettings(
    int? CpuPoolSize = null,
    int? IoThreads = null,
    long? MinFreeDiskMb = null,
    string? CacheDir = null,
    int? GdalCacheMb = null);
