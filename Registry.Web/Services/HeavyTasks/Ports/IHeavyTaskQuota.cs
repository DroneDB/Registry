#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// Quota and concurrency guard evaluated before enqueueing a heavy task
/// (spec §4.9). First failure wins.
/// </summary>
public interface IHeavyTaskQuota
{
    Task<HeavyTaskQuotaResult> EvaluateAsync(
        HeavyTaskSubmitRequest request, HeavyToolPlan plan, CancellationToken ct = default);
}
