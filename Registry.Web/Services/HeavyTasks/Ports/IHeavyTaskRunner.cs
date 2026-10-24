#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// Front door of the task substrate. Validates, plans, checks quota,
/// deduplicates and enqueues a heavy task onto the Hangfire <c>tasks</c> queue.
/// Authorization is performed by the caller (controller) before invocation.
/// </summary>
public interface IHeavyTaskRunner
{
    Task<HeavyTaskSubmitResult> SubmitAsync(HeavyTaskSubmitRequest request, CancellationToken ct = default);
}
