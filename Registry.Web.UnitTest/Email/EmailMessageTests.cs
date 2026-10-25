#nullable enable
using System;
using System.Linq;
using MimeKit;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email;
using Shouldly;

namespace Registry.Web.Test.Email;

/// <summary>
/// The parts of an e-mail that need no server: address rules, the MIME message, the branding and the text frame.
/// </summary>
[TestFixture]
public class EmailMessageTests
{
    private static SmtpSettings Smtp(Action<SmtpSettings>? tweak = null)
    {
        var settings = new SmtpSettings { Enabled = true, Host = "smtp.example.org", FromAddress = "registry@example.org" };
        tweak?.Invoke(settings);
        return settings;
    }

    private static EmailMessage Message(string? html = "<p>Hello</p>") =>
        new([new EmailAddress("jane@example.com", "Jane")], "Subject", "Hello", html);

    [TestCase("jane@example.com")]
    [TestCase(" jane.doe+tag@sub.example.co.uk ")]
    public void IsValid_AcceptsOneBareMailbox(string address) => EmailAddress.IsValid(address).ShouldBeTrue();

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("jane")]
    [TestCase("jane@")]
    [TestCase("Jane <jane@example.com>")]
    [TestCase("jane@example.com, john@example.com")]
    [TestCase("jane@example.com\r\nBcc: victim@example.com")]
    public void IsValid_RejectsAnythingElse(string? address) => EmailAddress.IsValid(address).ShouldBeFalse();

    [Test]
    public void MimeMessage_FromIsTheConfiguredAddress_NamedAfterTheApp_WhenNoFromName()
    {
        var mime = MimeMessageFactory.Create(Message(), Smtp(), "My Hub");

        var from = mime.From.Mailboxes.ShouldHaveSingleItem();
        from.Address.ShouldBe("registry@example.org");
        from.Name.ShouldBe("My Hub");
    }

    [Test]
    public void MimeMessage_FromName_WinsOverTheAppName()
    {
        var mime = MimeMessageFactory.Create(Message(), Smtp(s => s.FromName = "Registry Bot"), "My Hub");

        mime.From.Mailboxes.Single().Name.ShouldBe("Registry Bot");
    }

    [Test]
    public void MimeMessage_ReplyTo_IsTheMessages_ElseTheConfiguredOne()
    {
        var settings = Smtp(s => s.ReplyTo = "support@example.org");

        MimeMessageFactory.Create(Message(), settings, "Hub").ReplyTo.Mailboxes.Single().Address
            .ShouldBe("support@example.org");
        MimeMessageFactory.Create(Message() with { ReplyTo = new EmailAddress("owner@example.com") }, settings, "Hub")
            .ReplyTo.Mailboxes.Single().Address.ShouldBe("owner@example.com");
        MimeMessageFactory.Create(Message(), Smtp(), "Hub").ReplyTo.Count.ShouldBe(0);
    }

    [Test]
    public void MimeMessage_HasEveryRecipientList()
    {
        var message = Message() with
        {
            Cc = [new EmailAddress("cc@example.com")],
            Bcc = [new EmailAddress("bcc@example.com")]
        };

        var mime = MimeMessageFactory.Create(message, Smtp(), "Hub");

        mime.To.Mailboxes.Single().ShouldSatisfyAllConditions(
            m => m.Address.ShouldBe("jane@example.com"),
            m => m.Name.ShouldBe("Jane"));
        mime.Cc.Mailboxes.Single().Address.ShouldBe("cc@example.com");
        mime.Bcc.Mailboxes.Single().Address.ShouldBe("bcc@example.com");
        mime.Subject.ShouldBe("Subject");
    }

    [Test]
    public void MimeMessage_WithHtml_IsMultipartAlternative_TextFirst()
    {
        var mime = MimeMessageFactory.Create(Message(), Smtp(), "Hub");

        var alternative = mime.Body.ShouldBeOfType<MultipartAlternative>();
        alternative.Count.ShouldBe(2);
        ((TextPart)alternative[0]).IsPlain.ShouldBeTrue();
        ((TextPart)alternative[1]).IsHtml.ShouldBeTrue();
        mime.TextBody.ShouldBe("Hello");
        mime.HtmlBody.ShouldBe("<p>Hello</p>");
    }

    [Test]
    public void MimeMessage_WithoutHtml_IsPlainText()
    {
        var mime = MimeMessageFactory.Create(Message(html: null), Smtp(), "Hub");

        mime.Body.ShouldBeOfType<TextPart>().IsPlain.ShouldBeTrue();
    }

    [Test]
    public void Recipients_AreToCcAndBcc()
    {
        var message = Message() with { Cc = [new EmailAddress("cc@example.com")], Bcc = [new EmailAddress("b@example.com")] };

        message.Recipients().Select(r => r.Address).ShouldBe(["jane@example.com", "cc@example.com", "b@example.com"]);
    }

    [Test]
    public void Branding_DefaultsToDroneDB_WithoutLinks()
    {
        var branding = EmailBranding.From(new HubOptions(), new HttpHostOptions());

        branding.ShouldBe(new EmailBranding(EmailBranding.DefaultAppName, null, null));
    }

    [TestCase("/branding/logo.png", "https://hub.example.org/branding/logo.png")]
    [TestCase("https://cdn.example.org/logo.JPG", "https://cdn.example.org/logo.JPG")]
    [TestCase("/images/logo-banner.svg", null)]
    [TestCase("https://cdn.example.org/logo.svg", null)]
    [TestCase("branding/logo.png", null)]
    public void Branding_Logo_IsAnAbsoluteRasterUrl(string logo, string? expected)
    {
        var branding = EmailBranding.From(new HubOptions { AppName = " My Hub ", AppLogo = logo },
            new HttpHostOptions { ExternalUrlOverride = "https://hub.example.org" });

        branding.AppName.ShouldBe("My Hub");
        branding.BaseUrl.ShouldBe(new Uri("https://hub.example.org"));
        branding.LogoUrl?.ToString().ShouldBe(expected);
        (branding.LogoUrl == null).ShouldBe(expected == null);
    }

    [Test]
    public void Branding_RelativeLogo_WithoutPublicUrl_HasNoLogo()
    {
        EmailBranding.From(new HubOptions { AppLogo = "/branding/logo.png" }, new HttpHostOptions()).LogoUrl.ShouldBeNull();
    }

    [Test]
    public void Text_EndsWithTheSignature()
    {
        var withUrl = new EmailBranding("My Hub", new Uri("https://hub.example.org"), null);

        EmailText.Wrap("Hello\n\n", withUrl).ShouldBe("Hello\n\n-- \nMy Hub\nhttps://hub.example.org/\n");
        EmailText.Wrap("Hello", withUrl with { BaseUrl = null }).ShouldBe("Hello\n\n-- \nMy Hub\n");
    }
}
