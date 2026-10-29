using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.BuildNotifications;

namespace Registry.Web.Services.Ports;

/// <summary>Puts the final outcome of a build in the e-mail its requester gets, when they asked for it.</summary>
public interface IBuildNotificationRecorder
{
    /// <summary>
    /// Adds <paramref name="report"/> to the next build notification e-mail of the user who asked for the build, when
    /// e-mail is on and that user's preference wants it; the first waiting line of the user starts the digest window.
    /// Nothing happens for a build nobody asked for, and the same news is never added twice.
    /// </summary>
    Task RecordAsync(BuildReport report, CancellationToken ct);
}
