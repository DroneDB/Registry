#nullable enable
using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Email;

/// <summary>
/// One session with the configured SMTP server. It connects with the configured security, logs in when credentials
/// are set, runs the work and disconnects. Every MailKit failure becomes an <see cref="EmailDeliveryException"/>:
/// a <see cref="TransientEmailDeliveryException"/> when a later attempt can succeed. Both the sender and the
/// health check use it, so they connect the same way.
/// </summary>
internal static class SmtpConnection
{
    /// <summary>Runs <paramref name="work"/> in a session with the server of <paramref name="settings"/>.</summary>
    public static async Task RunAsync(SmtpSettings settings, Func<SmtpClient, CancellationToken, Task> work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(work);

        // The validator requires the host when e-mail is enabled
        var host = settings.Host ?? throw new EmailDeliveryException("AppSettings:Smtp:Host is not set");
        var server = $"{host}:{settings.Port}";

        // MailKit's client is not thread-safe and the volume is low: one client per session
        using var client = new SmtpClient();
        client.Timeout = (int)TimeSpan.FromSeconds(settings.TimeoutSeconds).TotalMilliseconds;
        if (!settings.ValidateServerCertificate)
            client.ServerCertificateValidationCallback = AcceptUntrustedChain;

        try
        {
            await client.ConnectAsync(host, settings.Port, SocketOptions(settings.Security), ct);
            if (!string.IsNullOrEmpty(settings.UserName))
                await client.AuthenticateAsync(settings.UserName, settings.Password ?? string.Empty, ct);

            await work(client, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (SmtpCommandException ex)
        {
            var reply = $"The mail server {server} replied {(int)ex.StatusCode}: {ex.Message}";
            throw IsTemporaryReply(ex) ? new TransientEmailDeliveryException(reply, ex) : new EmailDeliveryException(reply, ex);
        }
        catch (AuthenticationException ex)
        {
            throw new EmailDeliveryException(
                $"The mail server {server} refused the login of {settings.UserName}: {ex.Message}", ex);
        }
        catch (ServiceNotAuthenticatedException ex)
        {
            throw new EmailDeliveryException(
                $"The mail server {server} requires a login: set AppSettings:Smtp:UserName and Password ({ex.Message})", ex);
        }
        catch (SslHandshakeException ex)
        {
            throw new EmailDeliveryException(
                $"The TLS handshake with the mail server {server} failed (check AppSettings:Smtp:Security and the certificate): {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            // MailKit: the server does not offer STARTTLS, or no login mechanism the client supports
            throw new EmailDeliveryException(
                $"The mail server {server} does not support what the settings ask for: {ex.Message}", ex);
        }
        catch (ProtocolException ex)
        {
            throw new TransientEmailDeliveryException($"The session with the mail server {server} failed: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw Unreachable(server, ex);
        }
        catch (SocketException ex)
        {
            throw Unreachable(server, ex);
        }
        catch (TimeoutException ex)
        {
            throw Unreachable(server, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw Unreachable(server, ex);
        }
    }

    /// <summary>
    /// The check of a server whose certificate is not validated (<see cref="SmtpSettings.ValidateServerCertificate"/>
    /// false): a chain that does not lead to a trusted root (a self-signed certificate) is accepted. A certificate
    /// for another host name, or no certificate, is still refused.
    /// </summary>
    internal static bool AcceptUntrustedChain(object sender, X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors errors) =>
        (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None;

    /// <summary>The MailKit option of <paramref name="security"/>.</summary>
    internal static SecureSocketOptions SocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, "Unknown SMTP security")
    };

    /// <summary>A 4xx reply means "try again later" (RFC 5321); a 5xx reply is final.</summary>
    internal static bool IsTemporaryReply(SmtpCommandException ex) => (int)ex.StatusCode is >= 400 and < 500;

    private static TransientEmailDeliveryException Unreachable(string server, Exception ex) =>
        new($"Cannot reach the mail server {server}: {ex.Message}", ex);
}
