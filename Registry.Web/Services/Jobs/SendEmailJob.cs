using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Registry.Web.Exceptions;
using Registry.Web.Models.Email;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// Sends a queued e-mail. Only a transient failure is retried: the server unreachable, a timeout, a 4xx reply. The
/// delays add up to about four hours, enough for a mail server restart or a provider outage. A refused recipient or
/// login fails the job at once: the same message cannot succeed, and the Hangfire dashboard shows the reason.
/// </summary>
[AutomaticRetry(Attempts = 5, DelaysInSeconds = [60, 300, 900, 3600, 10800],
    OnlyOn = [typeof(TransientEmailDeliveryException)])]
public class SendEmailJob
{
    private readonly IEmailSender _sender;

    public SendEmailJob(IEmailSender sender)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    /// <summary>Sends <paramref name="message"/>.</summary>
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        _sender.SendAsync(message, cancellationToken);
}
