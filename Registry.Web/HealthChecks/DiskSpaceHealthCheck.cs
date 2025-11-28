using System;
using System.Collections.Generic;
using System.Linq;
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

        var info = CommonUtils.GetStorageInfo(_path);

        var data = new Dictionary<string, object>
        {
            {"StorageTotalSize", info?.TotalSize},
            {"StorageFreeSpace", info?.FreeSpace},
            {"StorageFreeSpacePerc", info?.FreeSpacePerc}
        };
            
        if (info != null && info.FreeSpacePerc <= _freeSpacePercWarningThreshold)
            return Task.FromResult(HealthCheckResult.Degraded("Low on available disk space", null, data));

        return Task.FromResult(HealthCheckResult.Healthy("Free disk space is fine", data));

    }
}