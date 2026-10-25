#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Email;

/// <summary>Sends through the configured SMTP server with MailKit.</summary>
public sealed class MailKitEmailSender : IEmailSender
{
    private readonly IOptions<SmtpSettings> _smtp;
    private readonly IOptions<HubOptions> _hub;
    private readonly ILogger<MailKitEmailSender> _logger;

    public MailKitEmailSender(IOptions<SmtpSettings> smtp, IOptions<HubOptions> hub, ILogger<MailKitEmailSender> logger)
    {
        _smtp = smtp ?? throw new ArgumentNullException(nameof(smtp));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mime = MimeMessageFactory.Create(message, _smtp.Value, EmailBranding.AppNameOf(_hub.Value));

        await SmtpConnection.RunAsync(_smtp.Value, (client, token) => client.SendAsync(mime, token), ct);

        // Addresses are personal data: the log keeps their number and domains only
        _logger.LogInformation("E-mail {MessageId} sent to {RecipientCount} recipient(s) at {RecipientDomains}",
            mime.MessageId, message.Recipients().Count(), EmailLog.Domains(message));
    }
}
