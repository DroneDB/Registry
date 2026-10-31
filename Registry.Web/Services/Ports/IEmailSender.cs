using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.Email;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The transport that hands one message to the mail server. Features use <see cref="IEmailService"/>; only it
/// and the queued e-mail job call this.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends <paramref name="message"/>.</summary>
    /// <exception cref="Exceptions.EmailDeliveryException">The server refused it (permanent).</exception>
    /// <exception cref="Exceptions.TransientEmailDeliveryException">The server was unreachable or busy.</exception>
    Task SendAsync(EmailMessage message, CancellationToken ct);
}
