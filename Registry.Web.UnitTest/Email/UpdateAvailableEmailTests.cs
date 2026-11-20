using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email;
using Registry.Web.Services.Email.Templates;
using Shouldly;

namespace Registry.Web.Test.Email;

/// <summary>
/// The update e-mail composed with the real Razor renderer: subject, both parts, the notes as text, the links.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UpdateAvailableEmailTests"
/// </summary>
[TestFixture]
public class UpdateAvailableEmailTests
{
    private const string Notes = "## Security\n- Fixes a flaw (v3.1.0)\n\n## Fixed\n- A fix with `code` & <b>tags</b> (v3.1.0)\n\n" +
                                 "All changes: https://github.com/DroneDB/Registry/compare/v3.0.0...v3.1.0";

    private static readonly EmailAddress Jane = new("jane@example.com");

    private static EmailComposer Composer(string? externalUrl = "https://hub.example.org") =>
        new(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(new HubOptions { AppName = "My Hub" }),
            Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = externalUrl }));

    private static UpdateAvailableEmailModel Model(bool security = true, string? notes = Notes,
        string? compare = "https://github.com/DroneDB/Registry/compare/v3.0.0...v3.1.0") =>
        new("3.0.0", "3.1.0", security, notes, compare);

    private static Task<EmailMessage> Compose(UpdateAvailableEmailModel model, string? externalUrl = "https://hub.example.org") =>
        Composer(externalUrl).ComposeAsync(UpdateAvailableEmailTemplate.Instance, model, [Jane]);

    [Test]
    public async Task Subject_NamesTheVersion_AndASecurityUpdate()
    {
        (await Compose(Model(security: false))).Subject.ShouldBe("Registry 3.1.0 is available");
        (await Compose(Model(security: true))).Subject.ShouldBe("Registry 3.1.0: security update");
    }

    [Test]
    public async Task TextPart_HasBothVersions_TheMarkdownNotes_TheHubLink_AndTheOptOutKey()
    {
        var text = (await Compose(Model())).TextBody;

        text.ShouldSatisfyAllConditions(
            t => t.ShouldContain("includes security fixes"),
            t => t.ShouldContain("Installed version: 3.0.0"),
            t => t.ShouldContain("Latest version: 3.1.0"),
            // The Markdown as it is, not converted
            t => t.ShouldContain(Notes),
            t => t.ShouldContain("Open My Hub: https://hub.example.org/"),
            t => t.ShouldContain("AppSettings:UpdateCheckEmail"));
    }

    [Test]
    public async Task HtmlPart_HasTheSameContent_AndTheNotesInOnePreWrapBlock_WithoutMarkdownRendering()
    {
        var html = (await Compose(Model())).HtmlBody.ShouldNotBeNull();

        html.ShouldSatisfyAllConditions(
            h => h.ShouldContain("<title>Registry 3.1.0: security update</title>"),
            h => h.ShouldContain("includes security fixes"),
            h => h.ShouldContain("<strong>3.0.0</strong>"),
            h => h.ShouldContain("<strong>3.1.0</strong>"),
            h => h.ShouldContain("AppSettings:UpdateCheckEmail"),
            h => h.ShouldContain("href=\"https://hub.example.org/\""),
            h => h.ShouldContain("href=\"https://github.com/DroneDB/Registry/compare/v3.0.0...v3.1.0\""),
            // One block, the notes encoded: what a mail client shows is the Markdown as it was written
            h => h.ShouldContain("white-space:pre-wrap"),
            h => h.ShouldContain("&amp; &lt;b&gt;tags&lt;/b&gt; (v3.1.0)"),
            h => System.Net.WebUtility.HtmlDecode(h).ShouldContain(Notes),
            h => h.ShouldNotContain("<h2"),
            h => h.ShouldNotContain("<ul"),
            h => h.ShouldNotContain("<li>"));
        System.Text.RegularExpressions.Regex.Count(html, "white-space:pre-wrap").ShouldBe(1);
    }

    [Test]
    public async Task AScriptInTheNotes_StaysText()
    {
        var message = await Compose(Model(notes: "## Fixed\n- <script>alert(1)</script> <img src=x onerror=alert(2)>"));

        message.HtmlBody.ShouldNotBeNull().ShouldSatisfyAllConditions(
            h => h.ShouldNotContain("<script"),
            h => h.ShouldNotContain("<img src=x"),
            h => h.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;"),
            h => h.ShouldContain("&lt;img src=x onerror=alert(2)&gt;"));
        // The plain part is text already
        message.TextBody.ShouldContain("<script>alert(1)</script>");
    }

    [Test]
    public async Task AScriptInTheVersion_StaysText()
    {
        var message = await Composer().ComposeAsync(UpdateAvailableEmailTemplate.Instance,
            new UpdateAvailableEmailModel("<i>3.0.0</i>", "<script>1</script>", false, null, null), [Jane]);

        message.HtmlBody.ShouldNotBeNull().ShouldNotContain("<script>");
        message.HtmlBody.ShouldNotContain("<i>");
    }

    [Test]
    public async Task WithoutExternalUrlOverride_ThereIsNoHubLink()
    {
        var message = await Compose(Model(), externalUrl: null);

        message.HtmlBody.ShouldNotBeNull().ShouldNotContain("https://hub.example.org");
        message.HtmlBody.ShouldNotContain("to see the update");
        message.TextBody.ShouldNotContain("Open My Hub");
        message.TextBody.ShouldNotContain("hub.example.org");
        // The link to the changes is the update service's, not the Hub's
        message.HtmlBody.ShouldContain("href=\"https://github.com/DroneDB/Registry/compare/");
    }

    [TestCase("javascript:alert(1)")]
    [TestCase("data:text/html,<script>1</script>")]
    [TestCase("/relative/path")]
    [TestCase("")]
    [TestCase(null)]
    public async Task ACompareUrl_ThatIsNotHttp_IsNotALink(string? compare)
    {
        var html = (await Compose(Model(notes: null, compare: compare), externalUrl: null)).HtmlBody.ShouldNotBeNull();

        html.ShouldNotContain("href=");
    }

    [Test]
    public async Task WithoutNotes_NoNotesBlockIsWritten()
    {
        var message = await Compose(Model(notes: null));

        message.HtmlBody.ShouldNotBeNull().ShouldNotContain("white-space:pre-wrap");
        message.TextBody.ShouldNotContain("What changed");
    }

    [Test]
    public async Task AnUnknownInstalledVersion_IsSaid()
    {
        var message = await Composer().ComposeAsync(UpdateAvailableEmailTemplate.Instance,
            new UpdateAvailableEmailModel(null, "3.1.0", false, null, null), [Jane]);

        message.TextBody.ShouldContain("Installed version: unknown");
    }
}
