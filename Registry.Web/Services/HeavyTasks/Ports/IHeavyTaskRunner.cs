#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>
/// Front door of the task substrate (spec §4). Validates, plans, checks quota,
/// deduplicates and enqueues a heavy task onto the Hangfire <c>tasks</c> queue.
/// Authorization is performed by the caller (controller) before invocation.
/// </summary>
public interface IHeavyTaskRunner
{
    Task<HeavyTaskSubmitResult> SubmitAsync(HeavyTaskSubmitRequest request, CancellationToken ct = default);

    /// <summary>
    /// System (non-user) submit used by the gradual migration of the legacy
    /// <c>EnqueueIndexed</c> build call sites. Bypasses quota and dedup, enqueues a
    /// <c>build</c> task with <c>UserId = null</c>.
    /// </summary>
    string SubmitSystemBuild(string orgSlug, string dsSlug, string? path, bool force, string? hash = null);
}
