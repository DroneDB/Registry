using System.Threading.Tasks;
using Registry.Web.Models.Email;
using Registry.Web.Services.BuildNotifications;

namespace Registry.Web.Services.Ports;

/// <summary>Writes the build notification e-mail of one user.</summary>
public interface IBuildNotificationComposer
{
    /// <summary>
    /// The e-mail to <paramref name="recipient"/> about the builds of <paramref name="model"/>, with the text an
    /// administrator saved for it (or the built-in one), in the shared layout and branding. Send it with
    /// <see cref="IEmailService.QueueAsync(EmailMessage, System.Threading.CancellationToken)"/>.
    /// </summary>
    Task<EmailMessage> ComposeAsync(EmailAddress recipient, BuildNotificationModel model);
}
