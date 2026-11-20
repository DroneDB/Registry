using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.Users;

namespace Registry.Web.Services.Ports;

/// <summary>Which task e-mails a user wants (FR-PRC-40), on any host that runs jobs.</summary>
public interface ITaskNotificationLevels
{
    /// <summary>The task choice of <paramref name="userId"/> (<see cref="BuildNotificationPreference.TasksOf"/>); none for an unknown user.</summary>
    Task<BuildNotificationLevel> TasksOfAsync(string userId, CancellationToken ct);
}
