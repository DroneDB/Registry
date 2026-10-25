using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Registry.Common;

namespace Registry.Web.HealthChecks;

public class DiskSpaceHealthCheck : IHealthCheck
{
    private readonly string _path;
    private readonly float _freeSpacePercWarningThreshold;

    public const float DefaultFreeSpacePercWarningThreshold = 0.1f;

    public DiskSpaceHealthCheck(string path, float freeSpacePercWarningThreshold = DefaultFreeSpacePercWarningThreshold)
    {
        _path = path;
        _freeSpacePercWarningThreshold = freeSpacePercWarningThreshold;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
    {

        // The path's own volume, the one disk-space API
        var volume = VolumeInfo.For(_path);
        float? freePerc = volume is { TotalBytes: > 0 } ? (float)volume.AvailableFreeBytes / volume.TotalBytes : null;

        var data = new Dictionary<string, object>
        {
            {"StorageTotalSize", volume?.TotalBytes},
            {"StorageFreeSpace", volume?.AvailableFreeBytes},
            {"StorageFreeSpacePerc", freePerc}
        };

        if (freePerc <= _freeSpacePercWarningThreshold)
            return Task.FromResult(HealthCheckResult.Degraded("Low on available disk space", null, data));

        return Task.FromResult(HealthCheckResult.Healthy("Free disk space is fine", data));

    }
}