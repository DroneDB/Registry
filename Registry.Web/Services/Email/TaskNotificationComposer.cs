using System;
using System.Threading.Tasks;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.Ports;
using Registry.Web.Services.TaskNotifications;

namespace Registry.Web.Services.Email;

/// <summary><see cref="ITaskNotificationComposer"/> with the current text of <see cref="TaskNotificationEmailTemplate"/>.</summary>
public sealed class TaskNotificationComposer : ITaskNotificationComposer
{
    private readonly IEmailComposer _composer;
    private readonly EditableEmailTemplates _templates;

    public TaskNotificationComposer(IEmailComposer composer, EditableEmailTemplates templates)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
    }

    /// <inheritdoc />
    public Task<EmailMessage> ComposeAsync(EmailAddress recipient, TaskNotificationModel model)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(model);

        var template = TaskNotificationEmailTemplate.Instance;
        var text = _templates.Current(template).Parsed;

        return _composer.ComposeAsync(template, new TaskNotificationEmailModel(model, text), [recipient]);
    }
}
