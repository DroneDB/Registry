using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.BuildNotifications;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Puts the final outcome of a build in the e-mail its requester gets, when they asked for it, and forgets the waits for
/// files of the entries that leave a dataset.
/// </summary>
public interface IBuildNotificationRecorder
{
    /// <summary>
    /// Adds <paramref name="report"/> to the next build notification e-mail of the user who asked for the build, when
    /// e-mail is on and that user's preference wants it; the first waiting line of the user starts the digest window.
    /// Nothing happens for a build nobody asked for, and the same news is never added twice.
    /// </summary>
    Task RecordAsync(BuildReport report, CancellationToken ct);

    /// <summary>
    /// The entries at <paramref name="paths"/> (and under them) left the dataset: deleted, or moved to another path or
    /// dataset. The waits for files of their builds end, so the same content put back waits as news again, told to whoever
    /// asks for its build then. Done whether e-mail is on or not (the waits recorded while it was on end too).
    /// </summary>
    Task EntriesLeftAsync(string orgSlug, string dsSlug, IReadOnlyCollection<string> paths, CancellationToken ct);
}
