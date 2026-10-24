using System;
using System.Text.Json;

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

        using var document = JsonDocument.Parse(json);
        var settings = document.RootElement.TryGetProperty("settings", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new FormatException("DDBGetEffectiveConfig: no 'settings' object");

        return new DdbEffectiveConfig(
            Number<long>(settings, "minFreeDiskMb") ?? throw new FormatException("DDBGetEffectiveConfig: no 'minFreeDiskMb'"),
            (int?)Number<long>(settings, "cpuPoolSize") ?? 0,
            (int?)Number<long>(settings, "ioThreads") ?? 0,
            settings.TryGetProperty("cacheDir", out var cacheDir) && cacheDir.ValueKind == JsonValueKind.String ? cacheDir.GetString() : null);
    }

    private static T? Number<T>(JsonElement parent, string field) where T : struct, System.Numerics.INumber<T> =>
        parent.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number
            ? T.CreateChecked(value.GetInt64())
            : null;
}
