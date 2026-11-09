using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;

namespace Registry.Web.Services.TaskNotifications;

/// <summary>
/// Tells the user who submitted a heavy task that it ended (FR-PRC-40, D-012): one e-mail per task, when e-mail is on and
/// the user's task preference (<see cref="BuildNotificationPreference.TasksField"/>: none, failed, all) wants the outcome.
/// It is not sent for a task of the system or of the build service (no user), for a child task, for the build tool (a
/// build notification, <see cref="BuildNotificationRecorder"/>), for a dataset that is gone or that the user can no longer
/// read, or to a user who cannot be e-mailed. The task end is recorded once in the outbox
/// (<see cref="BuildNotificationOutbox.AddTaskEndAsync"/>), so a state applied twice sends one e-mail. A task that failed
/// in the dataset it created links to the organization, not to that dataset, which its rollback deletes. The e-mail is
/// queued (<see cref="IEmailService"/>, whose job retries the mail server's transient failures itself).
/// </summary>
public sealed class TaskNotifier
{
    private readonly RegistryContext _db;
    private readonly IEmailService _email;
    private readonly ITaskNotificationLevels _levels;
    private readonly IUserRecipients _recipients;
    private readonly IUserDatasetAccess _datasets;
    private readonly ITaskNotificationComposer _composer;
    private readonly BuildNotificationOutbox _outbox;
    private readonly IHeavyToolRegistry? _tools;
    private readonly TimeProvider _time;
    private readonly ILogger<TaskNotifier> _logger;

    public TaskNotifier(RegistryContext db, IEmailService email, ITaskNotificationLevels levels, IUserRecipients recipients,
        IUserDatasetAccess datasets, ITaskNotificationComposer composer, BuildNotificationOutbox outbox, TimeProvider time,
        ILogger<TaskNotifier> logger, IHeavyToolRegistry? tools = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _levels = levels ?? throw new ArgumentNullException(nameof(levels));
        _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        _datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tools = tools;
    }

    /// <summary>Tells the submitter of <paramref name="task"/> that it ended, when they asked for that.</summary>
    /// <returns>Whether an e-mail was queued.</returns>
    public async Task<bool> NotifyAsync(FinishedTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Before any query: with e-mail off nothing could ever be sent
        if (!_email.IsEnabled)
            return false;

        var row = await _db.JobIndices.AsNoTracking().Where(j => j.JobId == task.JobId)
            .Select(j => new
            {
                j.UserId, j.ParentJobId, j.ToolId, j.ToolVersion, j.OrgSlug, j.DsSlug, j.TargetLabel, j.ErrorMessage,
                j.ProcessingAtUtc, j.SucceededAtUtc, j.FailedAtUtc, j.WriteRecordJson
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // A task of the system, of the build service or of a parent (a child of a workflow) is nobody's to be told about
        if (row == null || !UserTasks.IsUser(row.UserId) || row.ParentJobId != null || row.ToolId == HeavyToolIds.Build ||
            SlugKeyedRows.IsTombstone(row.DsSlug))
            return false;

        var userId = row.UserId!;
        var level = await _levels.TasksOfAsync(userId, ct).ConfigureAwait(false);
        if (!level.WantsTask(task.Succeeded))
            return false;

        var recipient = await _recipients.FindAsync(userId, ct).ConfigureAwait(false);
        if (recipient == null)
        {
            _logger.LogInformation("The end of task {TaskId} is not told to user {UserId}: the user cannot be e-mailed",
                task.JobId, userId);
            return false;
        }

        // The slugs, the files and the error of a dataset are not the business of who cannot read it any more
        if (!await _datasets.CanReadAsync(userId, row.OrgSlug, row.DsSlug, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("The end of task {TaskId} is not told to user {UserId}: the user cannot read {Org}/{Ds}",
                task.JobId, userId, row.OrgSlug, row.DsSlug);
            return false;
        }

        // The message of the task error (FR-XC-06) is the one the task list shows; one function made it
        var error = task.Succeeded ? null : BuildReport.ShortError(
            string.IsNullOrWhiteSpace(row.ErrorMessage) ? JobLogText.UserMessage(task.Failure, null) : row.ErrorMessage, null);
        // A task that failed in the dataset it created: its rollback deletes that dataset (U-114, U-302), so the e-mail must
        // not send the user to the Tasks tab of a page that will be gone
        var datasetRemoved = !task.Succeeded && TaskWriteRecord.Parse(row.WriteRecordJson)?.CreatedDataset != null;
        var title = _tools?.Resolve(row.ToolId, row.ToolVersion)?.Title ?? row.ToolId;

        var line = await _outbox.AddTaskEndAsync(userId, row.OrgSlug, row.DsSlug, task.JobId, row.ToolId, row.TargetLabel,
            task.Succeeded, error, ct).ConfigureAwait(false);
        if (line == null)
        {
            _logger.LogDebug("The end of task {TaskId} is already told to user {UserId}", task.JobId, userId);
            return false;
        }

        try
        {
            var end = (task.Succeeded ? row.SucceededAtUtc : row.FailedAtUtc) ?? _time.GetUtcNow().UtcDateTime;
            var duration = row.ProcessingAtUtc is { } start && end >= start ? end - start : (TimeSpan?)null;
            var message = await _composer.ComposeAsync(recipient,
                new TaskNotificationModel(recipient.DisplayName ?? userId, title, row.TargetLabel, row.OrgSlug, row.DsSlug,
                    task.Succeeded, error, duration, datasetRemoved)).ConfigureAwait(false);
            await _email.QueueAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Nothing was queued: the record goes, so a state applied again can still tell it
            await _outbox.RemoveAsync(line, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation("Task notification of task {TaskId} ({Tool}, {Outcome}) queued for user {UserId}",
            task.JobId, row.ToolId, task.Succeeded ? "succeeded" : "failed", userId);
        return true;
    }

    /// <summary>
    /// The failed task <paramref name="jobId"/> runs again (a user's retry keeps the job id): the end of that run is a new
    /// request, told even when it is the outcome already told. Done whether e-mail is on or not.
    /// </summary>
    public Task RunAgainAsync(string jobId, CancellationToken ct) => _outbox.RenewRequestAsync(jobId, ct);
}
