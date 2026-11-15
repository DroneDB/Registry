using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Models.Email;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;

namespace Registry.Web.Services.TaskNotifications;

/// <summary>
/// Who is told that a task ended (FR-PRC-40), decided when it ends: the user who submitted it, when their task
/// preference wants that outcome, they have an address to write to, and they can still read the dataset (the slugs, the
/// files and the error of a dataset are not the business of who cannot read it any more). The task counterpart of
/// <see cref="BuildNotifications.BuildNotificationAudience"/>.
/// </summary>
public sealed class TaskNotificationAudience
{
    private readonly ITaskNotificationLevels _levels;
    private readonly IUserRecipients _recipients;
    private readonly IUserDatasetAccess _datasets;
    private readonly ILogger<TaskNotificationAudience> _logger;

    public TaskNotificationAudience(ITaskNotificationLevels levels, IUserRecipients recipients,
        IUserDatasetAccess datasets, ILogger<TaskNotificationAudience> logger)
    {
        _levels = levels ?? throw new ArgumentNullException(nameof(levels));
        _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        _datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The address to tell <paramref name="userId"/> that task <paramref name="taskId"/> of
    /// <paramref name="orgSlug"/>/<paramref name="dsSlug"/> ended (<paramref name="succeeded"/>), or null when the user
    /// is not to be told.
    /// </summary>
    public async Task<EmailAddress?> RecipientAsync(string taskId, string userId, bool succeeded, string orgSlug,
        string dsSlug, CancellationToken ct)
    {
        var level = await _levels.TasksOfAsync(userId, ct).ConfigureAwait(false);
        if (!level.WantsTask(succeeded))
            return null;

        var recipient = await _recipients.FindAsync(userId, ct).ConfigureAwait(false);
        if (recipient == null)
        {
            _logger.LogInformation("The end of task {TaskId} is not told to user {UserId}: the user cannot be e-mailed",
                taskId, userId);
            return null;
        }

        if (!await _datasets.CanReadAsync(userId, orgSlug, dsSlug, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("The end of task {TaskId} is not told to user {UserId}: the user cannot read {Org}/{Ds}",
                taskId, userId, orgSlug, dsSlug);
            return null;
        }

        return recipient;
    }
}
