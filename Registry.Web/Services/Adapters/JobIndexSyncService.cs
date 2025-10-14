#nullable enable
using System.Threading.Tasks;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Service for syncing JobIndex states with Hangfire
/// </summary>
public class JobIndexSyncService
{
    private readonly IJobIndexQuery _jobIndexQuery;
    private readonly IJobIndexWriter _jobIndexWriter;
    private readonly ILogger<JobIndexSyncService> _logger;

    public JobIndexSyncService(IJobIndexQuery jobIndexQuery, IJobIndexWriter jobIndexWriter, ILogger<JobIndexSyncService> logger)
    {
        _jobIndexQuery = jobIndexQuery;
        _jobIndexWriter = jobIndexWriter;
        _logger = logger;
    }

    public async Task SyncJobIndexStates(PerformContext? context = null)
    {
        await HangfireUtils.SyncJobIndexStatesWrapper(context, _jobIndexQuery, _jobIndexWriter);
    }
}
