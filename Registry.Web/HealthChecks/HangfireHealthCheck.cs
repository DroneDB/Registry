using System.Threading;
using System.Threading.Tasks;
using Hangfire.Common;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Registry.Web.Utilities;

namespace Registry.Web.HealthChecks;

using Hangfire;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System;
using System.Threading;
using System.Threading.Tasks;

public class HangFireHealthCheck : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {

            var monitoringApi = JobStorage.Current.GetMonitoringApi();

            // Check if any Hangfire servers are running
            var servers = monitoringApi.Servers();
            if (servers == null || servers.Count == 0)
                return HealthCheckResult.Unhealthy("No Hangfire servers are running.");

            // Schedule a dummy job and wait for its completion
            var jobId = BackgroundJob.Enqueue(() => HangfireUtils.DummyJob(null));

            // Poll the job status until it's completed or failed (10 seconds timeout)
            for (var i = 0; i < 20; i++)
            {
                var jobDetails = monitoringApi.JobDetails(jobId);

                if (jobDetails != null && jobDetails.History.Count > 0)
                {
                    var state = jobDetails.History[0].StateName;

                    switch (state)
                    {
                        case "Succeeded":
                            return HealthCheckResult.Healthy("Hangfire is healthy and capable of processing jobs.");
                        case "Failed":
                            return HealthCheckResult.Unhealthy("Hangfire is running but test job failed.");
                    }
                }

                await Task.Delay(500, cancellationToken); // wait for 0.5 seconds before checking again
            }

            return HealthCheckResult.Degraded("Hangfire is running, but the job did not complete in time.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Exception during Hangfire health check: {ex.Message}");
        }
    }
}
