#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Exceptions;
using Registry.Web.Models.Email;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Email;

/// <summary>
/// The sender of a host where e-mail is disabled. <see cref="IEmailService"/> drops e-mail before it gets here, so
/// only a queued e-mail reaches it: one queued by a host whose settings enable e-mail. The job fails with the
/// reason, so it shows in the Hangfire dashboard and can be requeued once this host has the same settings.
/// </summary>
public sealed class DisabledEmailSender : IEmailSender
{
    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken ct) =>
        throw new EmailDeliveryException(
            "E-mail is disabled on this host (AppSettings:Smtp:Enabled is false): every Registry host that runs jobs needs the same SMTP settings");
}
