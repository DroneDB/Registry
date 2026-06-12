#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Default quota guard backed by <see cref="IJobIndexQuery"/> active-task counts
/// (spec §4.9). Checks, in order: estimated output size, global concurrency,
/// per-org concurrency, per-user concurrency/queue. Org daily-output budget is
/// configured but not enforced in Sprint 1 (requires a 24h artifact-sum query).
/// </summary>
public sealed class HeavyTaskQuota : IHeavyTaskQuota
{
    private readonly IJobIndexQuery _query;
    private readonly ProcessingPlatformSettings _settings;

    public HeavyTaskQuota(IJobIndexQuery query, IOptions<AppSettings> appSettings)
    {
        _query = query;
        _settings = appSettings.Value.ProcessingPlatform ?? new ProcessingPlatformSettings();
    }

    public async Task<HeavyTaskQuotaResult> EvaluateAsync(
        HeavyTaskSubmitRequest request, HeavyToolPlan plan, CancellationToken ct = default)
    {
        // 1. Estimated output size (413).
        if (plan.EstimatedOutputBytes is { } est && est > _settings.MaxEstimatedOutputBytesPerSubmit)
            return new HeavyTaskQuotaResult(HeavyTaskQuotaCode.TooLarge,
                $"Estimated output size ({est} bytes) exceeds the per-submit limit " +
                $"({_settings.MaxEstimatedOutputBytesPerSubmit} bytes).");

        // 2. Global concurrency.
        var global = await _query.CountActiveAsync(null, null, ct: ct);
        if (global >= _settings.MaxConcurrentTasksGlobal)
            return new HeavyTaskQuotaResult(HeavyTaskQuotaCode.Exceeded,
                "The platform is at maximum task concurrency; please retry shortly.");

        // 3. Per-org concurrency.
        var perOrg = await _query.CountActiveAsync(request.OrgSlug, null, ct: ct);
        if (perOrg >= _settings.MaxConcurrentTasksPerOrg)
            return new HeavyTaskQuotaResult(HeavyTaskQuotaCode.Exceeded,
                "Your organization has reached its concurrent task limit.");

        // 4. Per-user concurrency / queue.
        if (!string.IsNullOrEmpty(request.UserId))
        {
            var perUser = await _query.CountActiveAsync(null, request.UserId, ct: ct);
            if (perUser >= _settings.MaxQueuedTasksPerUser)
                return new HeavyTaskQuotaResult(HeavyTaskQuotaCode.Exceeded,
                    "You have too many queued or running tasks; wait for some to finish.");

            // 5. Per-tool per-user limit: bulk-download is expensive (ZIP compression +
            //    disk space). Reject if the user already has the configured number of
            //    active bulk-download tasks (default 1). This prevents a user from
            //    queueing multiple simultaneous ZIP compressions.
            if (request.ToolId == "bulk-download")
            {
                var perUserPerTool = await _query.CountActiveAsync(null, request.UserId, "bulk-download", ct);
                if (perUserPerTool >= _settings.MaxConcurrentBulkDownloadsPerUser)
                    return new HeavyTaskQuotaResult(HeavyTaskQuotaCode.Exceeded,
                        "You already have an active download archive being prepared. " +
                        "Please wait for it to complete before starting another.");
            }
        }

        return HeavyTaskQuotaResult.Ok;
    }
}
