#nullable enable
using System;
using System.Linq;
using MimeKit;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;

namespace Registry.Web.Services.Email;

/// <summary>
/// The MIME message of an <see cref="EmailMessage"/>. The sender is always the configured address. Its name is
/// <see cref="SmtpSettings.FromName"/>, or the application name. Reply-To is the message's, else the configured
/// one. A message with an HTML body is multipart/alternative, so a client that shows no HTML reads the text.
/// </summary>
internal static class MimeMessageFactory
{
    public static MimeMessage Create(EmailMessage message, SmtpSettings settings, string appName)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(settings);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(settings.FromName) ? appName : settings.FromName.Trim(),
            settings.FromAddress!.Trim()));

        var replyTo = message.ReplyTo ??
                      (string.IsNullOrWhiteSpace(settings.ReplyTo) ? null : new EmailAddress(settings.ReplyTo.Trim()));
        if (replyTo != null)
            mime.ReplyTo.Add(Mailbox(replyTo));

        mime.To.AddRange(message.To.Select(Mailbox));
        mime.Cc.AddRange(message.Cc.Select(Mailbox));
        mime.Bcc.AddRange(message.Bcc.Select(Mailbox));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        return mime;
    }

    private static MailboxAddress Mailbox(EmailAddress address) =>
        new(address.DisplayName ?? string.Empty, address.Address.Trim());
}
