#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.BuildNotifications;

namespace Registry.Web.Services.Ports;

/// <summary>Who a build notification goes to.</summary>
public interface IBuildRequesterResolver
{
    /// <summary>
    /// The user who asked for the build of <paramref name="report"/>: the job's user; for a task output (no user of its
    /// own) the user of the task; for an automatic retry the user whose build of the same content failed and was not
    /// built since. Null when nobody asked (a rescan, the completeness sweep, a system job).
    /// </summary>
    Task<BuildRequester?> ResolveAsync(BuildReport report, CancellationToken ct);
}
