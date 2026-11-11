using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Told about every update check that succeeded, so that the administrators can be informed of an update. The version
/// last notified is remembered in <see cref="IUpdateCheckCache"/>.
/// </summary>
public interface IUpdateNotifier
{
    /// <summary>
    /// Called after a check succeeded and its answer was remembered, whether or not an update is available
    /// (<see cref="UpdateInfoDto.IsUpdateAvailable"/>). A failure here is logged and does not fail the check.
    /// </summary>
    /// <param name="update">What the check found, as <see cref="IUpdateCheckService.GetUpdateInfo"/> reports it.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyAsync(UpdateInfoDto update, CancellationToken ct);
}
