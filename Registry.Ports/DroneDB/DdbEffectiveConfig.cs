using System;
using Newtonsoft.Json.Linq;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The libddb process settings in effect (<c>DDBGetEffectiveConfig</c>, ABI 1.1, XI-06): the
/// <c>DDBConfigure</c> values, else the environment variables, else libddb's defaults. Registry reads
/// the free-disk floor here so its own disk guards leave what a build needs.
/// </summary>
/// <param name="MinFreeDiskMb">Free MiB a build needs on the volume it writes to; 0: the check is off.</param>
/// <param name="CpuPoolSize">Worker threads libddb runs beside the calling threads.</param>
/// <param name="IoThreads">Threads of one disk-bound section.</param>
/// <param name="CacheDir">libddb's cache folder.</param>
public sealed record DdbEffectiveConfig(long MinFreeDiskMb, int CpuPoolSize, int IoThreads, string? CacheDir)
{
    /// <summary>Parses the <c>DDBGetEffectiveConfig()</c> JSON (unknown fields are ignored).</summary>
    /// <exception cref="FormatException">The JSON has no <c>settings</c> object or no <c>minFreeDiskMb</c>.</exception>
    public static DdbEffectiveConfig Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var settings = JObject.Parse(json)["settings"] as JObject
                       ?? throw new FormatException("DDBGetEffectiveConfig: no 'settings' object");

        return new DdbEffectiveConfig(
            (long?)settings["minFreeDiskMb"] ?? throw new FormatException("DDBGetEffectiveConfig: no 'minFreeDiskMb'"),
            (int?)settings["cpuPoolSize"] ?? 0,
            (int?)settings["ioThreads"] ?? 0,
            (string?)settings["cacheDir"]);
    }
}
