using System;
using System.Threading.Tasks;
using Registry.Web.Models.Email;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Email;

/// <summary><see cref="IBuildNotificationComposer"/> with the current text of <see cref="BuildNotificationEmailTemplate"/>.</summary>
public sealed class BuildNotificationComposer : IBuildNotificationComposer
{
    private readonly IEmailComposer _composer;
    private readonly EditableEmailTemplates _templates;

    public BuildNotificationComposer(IEmailComposer composer, EditableEmailTemplates templates)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
    }

    /// <inheritdoc />
    public Task<EmailMessage> ComposeAsync(EmailAddress recipient, BuildNotificationModel model)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(model);

        var template = BuildNotificationEmailTemplate.Instance;
        var text = _templates.Current(template).Parsed;

        return _composer.ComposeAsync(template, new BuildNotificationEmailModel(model, text), [recipient]);
    }
}
