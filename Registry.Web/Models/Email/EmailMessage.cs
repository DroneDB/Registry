#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace Registry.Web.Models.Email;

/// <summary>
/// One e-mail, ready to send. The sender comes from the SMTP settings. Hangfire stores a queued message as the job
/// argument (Json.NET, with type names where the runtime type differs from the declared one), so it holds plain
/// values only, no attachments, and copies every recipient list into an array: a list such as a collection
/// expression's compiler-generated type would be stored by its type name and could not be read back.
/// </summary>
public sealed record EmailMessage
{
    private readonly EmailAddress[] _to = [];
    private readonly EmailAddress[] _cc = [];
    private readonly EmailAddress[] _bcc = [];

    /// <param name="to">The recipients.</param>
    /// <param name="subject">The subject line.</param>
    /// <param name="textBody">The plain-text body (every message has one).</param>
    /// <param name="htmlBody">The HTML alternative of the body, when there is one.</param>
    public EmailMessage(IReadOnlyList<EmailAddress> to, string subject, string textBody, string? htmlBody = null)
    {
        To = to;
        Subject = subject;
        TextBody = textBody;
        HtmlBody = htmlBody;
    }

    /// <summary>The recipients.</summary>
    public IReadOnlyList<EmailAddress> To { get => _to; init => _to = [.. value ?? []]; }

    /// <summary>The carbon-copy recipients.</summary>
    public IReadOnlyList<EmailAddress> Cc { get => _cc; init => _cc = [.. value ?? []]; }

    /// <summary>The blind carbon-copy recipients.</summary>
    public IReadOnlyList<EmailAddress> Bcc { get => _bcc; init => _bcc = [.. value ?? []]; }

    /// <summary>The subject line.</summary>
    public string Subject { get; init; }

    /// <summary>The plain-text body (every message has one).</summary>
    public string TextBody { get; init; }

    /// <summary>The HTML alternative of the body, when there is one.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>Where replies go, instead of the configured reply-to address.</summary>
    public EmailAddress? ReplyTo { get; init; }

    /// <summary>Every recipient (to, cc, bcc). A method, so the job payload does not store it.</summary>
    public IEnumerable<EmailAddress> Recipients() => To.Concat(Cc).Concat(Bcc);

    /// <summary>Equal when every field is, the recipient lists compared item by item.</summary>
    public bool Equals(EmailMessage? other) =>
        other != null && _to.SequenceEqual(other._to) && _cc.SequenceEqual(other._cc) && _bcc.SequenceEqual(other._bcc) &&
        Subject == other.Subject && TextBody == other.TextBody && HtmlBody == other.HtmlBody && ReplyTo == other.ReplyTo;

    public override int GetHashCode() => System.HashCode.Combine(Subject, TextBody, HtmlBody, ReplyTo, _to.Length);
}
