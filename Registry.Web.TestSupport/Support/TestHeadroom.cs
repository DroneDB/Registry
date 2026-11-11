using Microsoft.Extensions.Logging.Abstractions;
using Registry.Adapters.Testing;
using Registry.Common;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Test.Support;

/// <summary>
/// <see cref="VolumeHeadroom"/> for tests: over <see cref="FakeDdbRuntime"/> (libddb's floor
/// is the <c>effective-config</c> fixture's 256 MiB), measuring the real volume or a given one.
/// </summary>
public static class TestHeadroom
{
    /// <summary>The real volume of each path, the fixture's floor.</summary>
    public static VolumeHeadroom Default { get; } = new(new FakeDdbRuntime(), NullLogger<VolumeHeadroom>.Instance);

    /// <summary>Every path is on one volume with <paramref name="availableBytes"/> free; libddb's floor is <paramref name="floorMb"/>.</summary>
    public static VolumeHeadroom WithFree(long availableBytes, long floorMb = 256) =>
        new(new FloorRuntime(floorMb), NullLogger<VolumeHeadroom>.Instance,
            _ => new VolumeInfo(long.MaxValue, availableBytes));

    private sealed class FloorRuntime(long floorMb) : FakeDdbRuntime
    {
        public override string GetEffectiveConfig() =>
            $$$"""{"hardwareThreads":8,"settings":{"cacheDir":"/c","cpuPoolSize":3,"ioThreads":2,"minFreeDiskMb":{{{floorMb}}}},"sources":{}}""";
    }
}
