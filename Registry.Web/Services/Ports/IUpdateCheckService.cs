using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The update check: once a day (and after the administrator changes what is declared) the deployment asks the update check
/// service which version is the latest, and remembers the answer. A check that fails changes nothing and is never an error
/// for the deployment.
/// </summary>
public interface IUpdateCheckService
{
    /// <summary>
    /// Asks the update check service now, remembers a good answer and tells <see cref="IUpdateNotifier"/> about it. Does
    /// nothing while the update check is switched off. A failure keeps the previous answer and is logged at debug level only.
    /// </summary>
    Task CheckForUpdatesAsync(CancellationToken ct);

    /// <summary>What is known: the installed and the latest version, the merged release notes, the licence request link.</summary>
    UpdateInfoDto GetUpdateInfo();
}
