using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.Hub;
using Registry.Web.Test.Support;
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

    // ---- branding: accent, icon and logo from the Hub settings ----

    private const string Public = "https://hub.example.org";

    private static EmailBranding Brand(HubOptions hub, string? externalUrl = Public) =>
        EmailBranding.From(hub, new HttpHostOptions { ExternalUrlOverride = externalUrl });

    private static HubOptions Themed(string? themeColor) => new() { Favicon = new FaviconOptions { ThemeColor = themeColor } };

    [TestCase("#ff6600", "#ff6600")]
    [TestCase(" #FF6600 ", "#ff6600")]
    [TestCase("#abc", "#aabbcc")]
    public void Branding_Accent_IsTheThemeColor(string themeColor, string expected) =>
        Brand(Themed(themeColor)).AccentColor.ShouldBe(expected);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("red")]
    [TestCase("#12345")]
    [TestCase("rgb(0,0,0)")]
    [TestCase("#fff;background:url(https://evil.example/x)")]
    [TestCase("\"><script>alert(1)</script>")]
    public void Branding_AnInvalidThemeColor_GivesTheBrandBlue(string? themeColor) =>
        Brand(Themed(themeColor)).AccentColor.ShouldBe(EmailStyles.BrandBlue);

    [Test]
    public void Branding_AccentOnlyTakesAHexColour()
    {
        Should.Throw<ArgumentException>(() => new EmailBranding("Hub", null, null) { AccentColor = "red;x:y" });
    }

    [TestCase("#0070e0", EmailStyles.LightText)]
    [TestCase("#000000", EmailStyles.LightText)]
    [TestCase("#7c3aed", EmailStyles.LightText)]
    [TestCase("#ffff00", EmailStyles.DarkText)]
    [TestCase("#f0f0f0", EmailStyles.DarkText)]
    [TestCase("#7dd3fc", EmailStyles.DarkText)]
    public void Branding_ButtonText_ReadsOnTheAccent(string accent, string text) =>
        Brand(Themed(accent)).AccentTextColor.ShouldBe(text);

    [TestCase("#0070e0")]
    [TestCase("#000000")]
    [TestCase("#7c3aed")]
    public void Branding_ADarkAccent_IsTheLinkColourAsItIs(string accent) =>
        Brand(Themed(accent)).LinkColor.ShouldBe(accent);

    [TestCase("#f2c200")]
    [TestCase("#ffff00")]
    [TestCase("#7dd3fc")]
    [TestCase("#ffffff")]
    public void Branding_ALightAccent_IsDarkenedForLinkText_UntilItReadsOnWhite(string accent)
    {
        var link = Brand(Themed(accent)).LinkColor;

        link.ShouldNotBe(accent);
        // A colour that is readable on white comes back unchanged
        EmailStyles.ReadableOnWhite(link).ShouldBe(link);
    }

    [Test]
    public void Branding_ALinkColourIsAlwaysHex() =>
        EmailStyles.NormalizeHex(EmailStyles.ReadableOnWhite("#f2c200")).ShouldNotBeNull();

    [Test]
    public void Branding_TheBrandBlue_IsThePrimaryColourOfTheHubPalette()
    {
        var palette = RepositoryFiles.PathOf("Registry.Web/ClientApp/webapp/js/libs/palette.js");
        if (!File.Exists(palette))
            Assert.Ignore("The Hub submodule is not checked out");

        var primary = Regex.Match(File.ReadAllText(palette), @"PRIMARY\s*=\s*Object\.freeze\(\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        primary.Success.ShouldBeTrue("palette.js declares PRIMARY");
        var shade = Regex.Match(primary.Groups["body"].Value, @"\b500\s*:\s*'(?<hex>#[0-9a-fA-F]{6})'");
        shade.Success.ShouldBeTrue("PRIMARY has a 500 shade");

        shade.Groups["hex"].Value.ToLowerInvariant().ShouldBe(EmailStyles.BrandBlue);
    }

    [Test]
    public void Branding_ARasterLogo_IsTheLogo_AndNoIcon()
    {
        var branding = Brand(new HubOptions
        {
            AppName = "Acme", AppLogo = "/branding/logo.png",
            Favicon = new FaviconOptions { AppleTouchIcon = "/branding/apple-touch-icon.png" }
        });

        branding.LogoUrl.ShouldBe(new Uri("https://hub.example.org/branding/logo.png"));
        branding.IconUrl.ShouldBeNull();
    }

    [Test]
    public void Branding_AnSvgLogo_FallsBackToTheAppleTouchIcon_ThenTheFavicon()
    {
        var withBoth = Brand(new HubOptions
        {
            AppName = "Acme", AppLogo = "/branding/logo.svg",
            Favicon = new FaviconOptions { AppleTouchIcon = "/branding/apple-touch-icon.png", Favicon32 = "/branding/favicon-32.png" }
        });
        withBoth.LogoUrl.ShouldBeNull();
        withBoth.IconUrl.ShouldBe(new Uri("https://hub.example.org/branding/apple-touch-icon.png"));

        var faviconOnly = Brand(new HubOptions
        {
            AppName = "Acme", Favicon = new FaviconOptions { AppleTouchIcon = "/branding/icon.svg", Favicon32 = "https://cdn.example.org/f32.png" }
        });
        faviconOnly.IconUrl.ShouldBe(new Uri("https://cdn.example.org/f32.png"));
    }

    [TestCase(null)]
    [TestCase(EmailBranding.StockAppLogo)]
    public void Branding_AStockInstance_ShowsTheBuiltInLogo(string? appLogo)
    {
        var branding = Brand(new HubOptions { AppLogo = appLogo });

        branding.AppName.ShouldBe(EmailBranding.DefaultAppName);
        branding.LogoUrl.ShouldBe(new Uri("https://hub.example.org/images/email-logo.png"));
        branding.IconUrl.ShouldBeNull();
    }

    [Test]
    public void Branding_TheBuiltInLogo_FollowsAPublicUrlWithAPath()
    {
        Brand(new HubOptions(), "https://example.org/hub").LogoUrl.ShouldBe(new Uri("https://example.org/hub/images/email-logo.png"));
    }

    [Test]
    public void Branding_AWhiteLabelledInstance_NeverShowsTheDroneDBLogo()
    {
        // Its own name: the name as text
        var named = Brand(new HubOptions { AppName = "Acme Survey", AppLogo = EmailBranding.StockAppLogo });
        named.LogoUrl.ShouldBeNull();
        named.IconUrl.ShouldBeNull();
        named.AppName.ShouldBe("Acme Survey");

        // Its own SVG logo, or its own icon class: text too
        Brand(new HubOptions { AppLogo = "/branding/acme.svg" }).LogoUrl.ShouldBeNull();
        Brand(new HubOptions { AppIcon = "fa fa-plane" }).LogoUrl.ShouldBeNull();
    }

    [Test]
    public void Branding_WithoutAPublicUrl_IsTextOnly()
    {
        Brand(new HubOptions(), externalUrl: null).ShouldBe(new EmailBranding(EmailBranding.DefaultAppName, null, null));
        Brand(new HubOptions { Favicon = new FaviconOptions { AppleTouchIcon = "/branding/i.png" } }, externalUrl: null)
            .IconUrl.ShouldBeNull();
    }

    private static EmailComposer Composer(HubOptions hub) =>
        new(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance, Microsoft.Extensions.Options.Options.Create(hub),
            Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = Public }));

    [Test]
    public async Task TheTestAndTheUpdateEmails_GetTheBrandingFromTheSharedLayout()
    {
        var hub = new HubOptions
        {
            AppName = "Acme", AppLogo = "/branding/acme.svg",
            Favicon = new FaviconOptions { ThemeColor = "#FF6600", AppleTouchIcon = "/branding/apple-touch-icon.png" }
        };
        var to = new[] { new EmailAddress("jane@example.com") };

        var test = await Composer(hub).ComposeAsync(TestEmailTemplate.Instance, new TestEmailModel("admin", DateTimeOffset.UnixEpoch), to);
        var update = await Composer(hub).ComposeAsync(UpdateAvailableEmailTemplate.Instance,
            new UpdateAvailableEmailModel("3.0.0", "3.1.0", false, null, "https://github.com/DroneDB/Registry/compare/a...b"), to);

        foreach (var html in new[] { test.HtmlBody!, update.HtmlBody! })
            html.ShouldSatisfyAllConditions(
                h => h.ShouldContain("bgcolor=\"#ff6600\""),
                h => h.ShouldContain("<img src=\"https://hub.example.org/branding/apple-touch-icon.png\""),
                h => h.ShouldContain(">Acme</td>"),
                h => h.ShouldNotContain("email-logo.png"));
        // Orange is too light for link text on white: links use the same hue, darkened until it reads
        update.HtmlBody!.ShouldContain($"style=\"color:{EmailStyles.ReadableOnWhite("#ff6600")};");
        update.HtmlBody!.ShouldNotContain("#2563eb");
    }

    [Test]
    public async Task AStockInstance_EmailHasTheBuiltInLogo_InTheBrandBlue()
    {
        var html = (await Composer(new HubOptions { AppLogo = EmailBranding.StockAppLogo })
            .ComposeAsync(TestEmailTemplate.Instance, new TestEmailModel("admin", DateTimeOffset.UnixEpoch), [new EmailAddress("j@example.com")]))
            .HtmlBody!;

        html.ShouldContain("<img src=\"https://hub.example.org/images/email-logo.png\" alt=\"DroneDB\" height=\"40\"");
        html.ShouldContain($"bgcolor=\"{EmailStyles.BrandBlue}\"");
    }

    [Test]
    public void HubLinks_AreUnderThePublicUrl_AndNullWithoutIt()
    {
        var root = new Uri("https://hub.example.org");

        HubLinks.DatasetPath("acme", "site-a").ShouldBe("/r/acme/site-a");
        HubLinks.Dataset(root, "acme", "site-a").ShouldBe(new Uri("https://hub.example.org/r/acme/site-a"));
        HubLinks.Dataset(new Uri("https://example.org/hub/"), "acme", "site-a").ShouldBe(new Uri("https://example.org/hub/r/acme/site-a"));
        HubLinks.NotificationPreferences(root)!.ToString().ShouldBe("https://hub.example.org/r?preferences=notifications");
        HubLinks.Dataset(null, "acme", "site-a").ShouldBeNull();
        HubLinks.NotificationPreferences(null).ShouldBeNull();
    }

    [Test]
    public void Text_EndsWithTheSignature()
    {
        var withUrl = new EmailBranding("My Hub", new Uri("https://hub.example.org"), null);

        EmailText.Wrap("Hello\n\n", withUrl).ShouldBe("Hello\n\n-- \nMy Hub\nhttps://hub.example.org/\n");
        EmailText.Wrap("Hello", withUrl with { BaseUrl = null }).ShouldBe("Hello\n\n-- \nMy Hub\n");
    }
}
