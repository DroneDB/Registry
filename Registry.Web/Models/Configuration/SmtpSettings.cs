#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>How the connection to the SMTP server is secured.</summary>
public enum SmtpSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS; the send fails when the server does not offer it (port 587).</summary>
    StartTls,

    /// <summary>TLS from the first byte (implicit TLS, port 465).</summary>
    SslOnConnect,

    /// <summary>No encryption: only for a relay on the same host or a trusted network (port 25).</summary>
    None
}

/// <summary>
/// The outgoing e-mail server (the <c>AppSettings:Smtp</c> section). E-mail is off until
/// <see cref="Enabled"/> is set; while it is off every e-mail is logged and dropped.
/// </summary>
public sealed class SmtpSettings
{
    /// <summary>Sends e-mail through the server below.</summary>
    public bool Enabled { get; set; }

    /// <summary>SMTP server host name. Required when enabled.</summary>
    public string? Host { get; set; }

    /// <summary>SMTP server port: 587 for STARTTLS, 465 for SSL on connect, 25 for a local relay.</summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// How the connection is secured. There is no "STARTTLS when available" mode: a server (or a man in
    /// the middle) that hides STARTTLS would silently get the credentials and the messages in clear text.
    /// </summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>
    /// Validates the server certificate chain. Disable only for a test server with a self-signed certificate: the
    /// certificate must still be for <see cref="Host"/>.
    /// </summary>
    public bool ValidateServerCertificate { get; set; } = true;

    /// <summary>User name of the SMTP account. Empty: no authentication (with <see cref="Password"/> empty too).</summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Password of the SMTP account. Never commit it in plain text: set it with the
    /// <c>AppSettings__Smtp__Password</c> environment variable or a secret store.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>Address the e-mails are sent from. Required when enabled.</summary>
    public string? FromAddress { get; set; }

    /// <summary>Display name of the sender. Empty: the Hub application name.</summary>
    public string? FromName { get; set; }

    /// <summary>Address replies go to, when it is not the sender's.</summary>
    public string? ReplyTo { get; set; }

    /// <summary>Timeout of each SMTP operation, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
