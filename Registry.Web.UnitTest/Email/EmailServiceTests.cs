using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test.Email;

/// <summary>
/// <see cref="EmailService"/> and <see cref="EmailComposer"/>: composing with the real Razor renderer, the queue
/// and send-now paths, the checks, and the drop while e-mail is disabled.
/// </summary>
[TestFixture]
public class EmailServiceTests
{
    private static readonly EmailAddress Jane = new("jane@example.com", "Jane");
    private static readonly TestEmailModel Model = new("admin", new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    private Mock<IEmailSender> _sender = null!;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private Expression<Func<SendEmailJob, Task>>? _enqueued;

    [SetUp]
    public void SetUp()
    {
        _sender = new Mock<IEmailSender>(MockBehavior.Strict);
        _jobs = new Mock<IBackgroundJobsProcessor>(MockBehavior.Strict);
        _enqueued = null;
        _jobs.Setup(j => j.Enqueue(It.IsAny<Expression<Func<SendEmailJob, Task>>>()))
            .Callback<Expression<Func<SendEmailJob, Task>>>(e => _enqueued = e)
            .Returns("42");
    }

    private static EmailComposer Composer(string? appName = "My Hub", string? externalUrl = "https://hub.example.org") =>
        new(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(new HubOptions { AppName = appName }),
            Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = externalUrl }));

    private EmailService Service(bool enabled, IEmailComposer? composer = null) =>
        new(Microsoft.Extensions.Options.Options.Create(new SmtpSettings { Enabled = enabled }), composer ?? Composer(), _sender.Object, _jobs.Object,
            NullLogger<EmailService>.Instance);

    /// <summary>The message the queued job carries, as Hangfire stores the call.</summary>
    private EmailMessage EnqueuedMessage() =>
        Job.FromExpression(_enqueued.ShouldNotBeNull()).Args[0].ShouldBeOfType<EmailMessage>();

    [Test]
    public async Task Compose_PutsTheBodyInTheLayout()
    {
        var message = await Composer().ComposeAsync(TestEmailTemplate.Instance, Model, [Jane]);

        message.To.ShouldBe([Jane]);
        message.Subject.ShouldBe("My Hub test e-mail");
        message.HtmlBody.ShouldNotBeNull().ShouldSatisfyAllConditions(
            html => html.ShouldStartWith("<!DOCTYPE html>"),
            html => html.ShouldContain("<title>My Hub test e-mail</title>"),
            html => html.ShouldContain("This is a test e-mail from My Hub."),
            html => html.ShouldContain("Sent at the request of admin on 2026-10-25 12:00:00Z."),
            html => html.ShouldContain("href=\"https://hub.example.org/\""));
        message.TextBody.ShouldStartWith("This is a test e-mail from My Hub.");
        message.TextBody.ShouldEndWith("\n-- \nMy Hub\nhttps://hub.example.org/\n");
    }

    [Test]
    public async Task Compose_EncodesTheModel()
    {
        var message = await Composer().ComposeAsync(TestEmailTemplate.Instance,
            Model with { RequestedBy = "<script>alert(1)</script>" }, [Jane]);

        message.HtmlBody.ShouldNotBeNull().ShouldNotContain("<script>");
        message.HtmlBody.ShouldContain("&lt;script&gt;");
    }

    [Test]
    public async Task Compose_WithoutPublicUrl_HasNoLink()
    {
        var message = await Composer(appName: null, externalUrl: null).ComposeAsync(TestEmailTemplate.Instance, Model, [Jane]);

        message.Subject.ShouldBe("DroneDB test e-mail");
        message.HtmlBody.ShouldNotBeNull().ShouldNotContain("href=");
        message.TextBody.ShouldEndWith("\n-- \nDroneDB\n");
    }

    [Test]
    public async Task Queue_EnqueuesTheComposedMessage()
    {
        var jobId = await Service(enabled: true).QueueAsync(TestEmailTemplate.Instance, Model, [Jane]);

        jobId.ShouldBe("42");
        var message = EnqueuedMessage();
        message.To.ShouldBe([Jane]);
        message.Subject.ShouldBe("My Hub test e-mail");
        message.HtmlBody.ShouldNotBeNull();
    }

    [Test]
    public async Task Queue_AMessage_EnqueuesIt()
    {
        var message = new EmailMessage([Jane], "Hi", "Hello");

        (await Service(enabled: true).QueueAsync(message)).ShouldBe("42");

        EnqueuedMessage().ShouldBe(message);
    }

    [Test]
    public async Task SendNow_HandsTheMessageToTheSender()
    {
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        (await Service(enabled: true).SendNowAsync(TestEmailTemplate.Instance, Model, [Jane])).ShouldBeTrue();

        _sender.Verify(s => s.SendAsync(It.Is<EmailMessage>(m => m.To.Single() == Jane), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SendNow_PassesTheSenderFailureOn()
    {
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmailDeliveryException("550 no such user"));

        (await Should.ThrowAsync<EmailDeliveryException>(() =>
            Service(enabled: true).SendNowAsync(new EmailMessage([Jane], "Hi", "Hello")))).Message.ShouldBe("550 no such user");
    }

    [Test]
    public async Task Disabled_DropsEverything_WithoutComposing()
    {
        var composer = new Mock<IEmailComposer>(MockBehavior.Strict);
        var service = Service(enabled: false, composer.Object);
        var message = new EmailMessage([Jane], "Hi", "Hello");

        service.IsEnabled.ShouldBeFalse();
        (await service.QueueAsync(TestEmailTemplate.Instance, Model, [Jane])).ShouldBeNull();
        (await service.QueueAsync(message)).ShouldBeNull();
        (await service.SendNowAsync(TestEmailTemplate.Instance, Model, [Jane])).ShouldBeFalse();
        (await service.SendNowAsync(message)).ShouldBeFalse();

        _jobs.Verify(j => j.Enqueue(It.IsAny<Expression<Func<SendEmailJob, Task>>>()), Times.Never);
    }

    [Test]
    public async Task AMessageWithoutRecipient_IsRefused()
    {
        var ex = await Should.ThrowAsync<BadRequestException>(() =>
            Service(enabled: true).QueueAsync(new EmailMessage([], "Hi", "Hello")));

        ex.Message.ShouldContain("no recipient");
    }

    [Test]
    public async Task AnInvalidAddress_IsRefused_AndNamed()
    {
        var message = new EmailMessage([Jane], "Hi", "Hello") { Cc = [new EmailAddress("not-an-address")] };

        var ex = await Should.ThrowAsync<BadRequestException>(() => Service(enabled: true).SendNowAsync(message));

        ex.Message.ShouldContain("not-an-address");
        _sender.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AnInvalidReplyTo_IsRefused()
    {
        var message = new EmailMessage([Jane], "Hi", "Hello") { ReplyTo = new EmailAddress("Owner <o@example.com>") };

        await Should.ThrowAsync<BadRequestException>(() => Service(enabled: true).QueueAsync(message));
    }

    [Test]
    public async Task AMessageWithoutSubject_IsRefused()
    {
        await Should.ThrowAsync<BadRequestException>(() =>
            Service(enabled: true).QueueAsync(new EmailMessage([Jane], " ", "Hello")));
    }
}
