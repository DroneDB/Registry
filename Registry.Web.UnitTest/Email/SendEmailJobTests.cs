using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using MailKit.Net.Smtp;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email;
using Registry.Web.Services.Jobs;
using Shouldly;

namespace Registry.Web.Test.Email;

/// <summary>
/// The queued path and the failure rules: the job's retry policy, its payload in the job storage, which SMTP
/// failures are transient, and the sender of a host where e-mail is disabled.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SendEmailJobTests
{
    private static readonly EmailMessage Message = new([new EmailAddress("jane@example.com", "Jane")], "Hi", "Hello", "<p>Hello</p>")
    {
        Cc = [new EmailAddress("cc@example.com")],
        ReplyTo = new EmailAddress("owner@example.com")
    };

    private static Job SendJob() => Job.FromExpression<SendEmailJob>(j => j.SendAsync(Message, CancellationToken.None));

    [Test]
    public void Job_RetriesOnlyTransientFailures()
    {
        var retry = JobFilterProviders.Providers.GetFilters(SendJob()).Select(f => f.Instance)
            .OfType<AutomaticRetryAttribute>().ShouldHaveSingleItem();

        retry.Attempts.ShouldBe(5);
        retry.OnlyOn.ShouldBe([typeof(TransientEmailDeliveryException)]);
        typeof(TransientEmailDeliveryException).IsAssignableTo(typeof(EmailDeliveryException)).ShouldBeTrue();
    }

    [Test]
    public void Job_Payload_RoundTripsThroughTheJobStorage()
    {
        // The serializer settings of the hosts (RegistryJobsServiceCollectionExtensions)
        GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings();

        var payload = InvocationData.SerializeJob(SendJob()).SerializePayload();
        var job = InvocationData.DeserializePayload(payload).DeserializeJob();

        job.Args[0].ShouldBeOfType<EmailMessage>().ShouldBe(Message);
        payload.ShouldNotContain("Recipients");
    }

    [TestCase(SmtpStatusCode.MailboxBusy, true)]
    [TestCase(SmtpStatusCode.InsufficientStorage, true)]
    [TestCase(SmtpStatusCode.ServiceNotAvailable, true)]
    [TestCase(SmtpStatusCode.MailboxUnavailable, false)]
    [TestCase(SmtpStatusCode.AuthenticationRequired, false)]
    [TestCase(SmtpStatusCode.TransactionFailed, false)]
    public void Reply_4xxIsTransient_5xxIsPermanent(SmtpStatusCode status, bool transient)
    {
        SmtpConnection.IsTemporaryReply(new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, status, "reply"))
            .ShouldBe(transient);
    }

    [Test]
    public async Task UnreachableServer_IsTransient()
    {
        // A port that was free a moment ago: the connection is refused
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var settings = new SmtpSettings
        {
            Enabled = true, Host = "127.0.0.1", Port = port, Security = SmtpSecurity.None, TimeoutSeconds = 5,
            FromAddress = "registry@example.org"
        };

        var ex = await Should.ThrowAsync<TransientEmailDeliveryException>(() =>
            SmtpConnection.RunAsync(settings, (_, _) => Task.CompletedTask, CancellationToken.None));

        ex.IsTransient.ShouldBeTrue();
        ex.StatusCode.ShouldBe(502);
        ex.Message.ShouldContain($"127.0.0.1:{port}");
    }

    [Test]
    public async Task DisabledSender_FailsPermanently()
    {
        var ex = await Should.ThrowAsync<EmailDeliveryException>(() =>
            new DisabledEmailSender().SendAsync(Message, CancellationToken.None));

        ex.ShouldNotBeOfType<TransientEmailDeliveryException>();
        ex.IsTransient.ShouldBeFalse();
        ex.Message.ShouldContain("AppSettings:Smtp:Enabled");
    }

    [TestCase(SslPolicyErrors.None, true)]
    [TestCase(SslPolicyErrors.RemoteCertificateChainErrors, true)]
    [TestCase(SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    [TestCase(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, false)]
    [TestCase(SslPolicyErrors.RemoteCertificateNotAvailable, false)]
    public void UnvalidatedCertificate_StillMatchesTheHostName(SslPolicyErrors errors, bool accepted)
    {
        SmtpConnection.AcceptUntrustedChain(this, null, null, errors).ShouldBe(accepted);
    }

    [TestCase(SmtpSecurity.StartTls, MailKit.Security.SecureSocketOptions.StartTls)]
    [TestCase(SmtpSecurity.SslOnConnect, MailKit.Security.SecureSocketOptions.SslOnConnect)]
    [TestCase(SmtpSecurity.None, MailKit.Security.SecureSocketOptions.None)]
    public void Security_MapsToMailKit(SmtpSecurity security, MailKit.Security.SecureSocketOptions expected)
    {
        SmtpConnection.SocketOptions(security).ShouldBe(expected);
    }
}
