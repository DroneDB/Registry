using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Registry.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.HealthChecks;

public class DdbHealthCheck : IHealthCheck
{

    private readonly IDdbManager _ddbManager;

    public DdbHealthCheck(IDdbManager ddbManager)
    {
        _ddbManager = ddbManager;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {

        var tempOrg = "test-" + Guid.NewGuid();
        var tempDs = Guid.NewGuid();

        var data = new Dictionary<string, object>
        {
            {"TempOrg", tempOrg},
            {"TempDs", tempDs.ToString()},
            {"Provider", _ddbManager.GetType().FullName}
        };

        var ddb = _ddbManager.Get(tempOrg, tempDs);

        try
        {
            var version = ddb.Version;
            if (string.IsNullOrWhiteSpace(version))
                return HealthCheckResult.Unhealthy("Cannot get ddb version", null, data);

            data.Add("DdbVersion", version);

            var entries = ddb.Search(null, true);

            if (entries == null || entries.Any())
                return HealthCheckResult.Unhealthy("Something wrong with ddb behaviour", null, data);

            return HealthCheckResult.Healthy("Ddb is working properly", data);

        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Exception while testing ddb: " + ex.Message, ex, data);
        }
        finally
        {
            _ddbManager.Delete(tempOrg);
        }

    }
}