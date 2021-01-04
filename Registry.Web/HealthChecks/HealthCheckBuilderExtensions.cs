using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Registry.Web.HealthChecks
{

    public static class HealthCheckBuilderExtensions
    {
        const string DefaultName = "Disk space health check";

        public static IHealthChecksBuilder AddDiskSpaceHealthCheck(
            this IHealthChecksBuilder builder,
            string path,
            string name = default,
            HealthStatus? failureStatus = default,
            IEnumerable<string> tags = default)
        {
            return builder.Add(new HealthCheckRegistration(
                name ?? DefaultName,
                sp => new DiskSpaceHealthCheck(path),
                failureStatus,
                tags));
        }
    }
}
