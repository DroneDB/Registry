using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.TaskNotifications;
using Shouldly;

namespace Registry.Web.Test.TaskNotifications;

/// <summary>
/// The task notification e-mail (FR-PRC-40) composed with the real Razor renderer: the built-in text for a task that failed
/// and for one that succeeded, the label (FR-XC-25), the error (FR-XC-06), the duration, the link to the Tasks tab, the
/// escaping, and a text an administrator could write.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~TaskNotificationEmailTests"
/// </summary>
[TestFixture]
public class TaskNotificationEmailTests
{
    private const string BaseUrl = "https://hub.example.org";
    private static readonly EmailAddress Jane = new("jane@example.com", "jane");

    private static EmailComposer Composer(string? externalUrl = BaseUrl) =>
        new(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(new HubOptions { AppName = "My Hub" }),
            Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = externalUrl }));

    private static TaskNotificationModel Failed(string error = "The processing node ran out of memory") =>
        new("jane", "Photogrammetry (NodeODX)", "flight-1/*.jpg", "acme", "site-a", false, error,
            TimeSpan.FromSeconds(17 * 60 + 4));

    private static TaskNotificationModel Succeeded() =>
        new("jane", "Extract archive", null, "acme", "site-a", true, null, TimeSpan.FromSeconds(45));

    private static ParsedEmailTemplate Text(EmailTemplateText? text = null) =>
        ParsedEmailTemplate.Parse(TaskNotificationEmailTemplate.Instance, text ?? TaskNotificationEmailTemplate.Instance.BuiltInText);

    private static Task<EmailMessage> Compose(TaskNotificationModel model, ParsedEmailTemplate? text = null,
        string? externalUrl = BaseUrl) =>
        Composer(externalUrl).ComposeAsync(TaskNotificationEmailTemplate.Instance,
            new TaskNotificationEmailModel(model, text ?? Text()), [Jane]);

    [Test]
    public async Task AFailedTask_SubjectAndTextPart_CarryTheLabel_TheError_TheDuration_AndTheTasksLink()
    {
        var message = await Compose(Failed());

        message.Subject.ShouldBe("My Hub: Photogrammetry (NodeODX) failed");
        message.TextBody.ShouldBe(
            """
            Hello jane,

            Your task "Photogrammetry (NodeODX)" on acme/site-a failed.

            Files: flight-1/*.jpg

            Reason: The processing node ran out of memory

            Duration: 17 min 4 s

            https://hub.example.org/r/acme/site-a?tab=tasks

            To change which task e-mails you get, open your notification settings: https://hub.example.org/r?preferences=notifications

            -- 
            My Hub
            https://hub.example.org/

            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task ASucceededTask_HasNoReason_AndNoFiles_WhenItHasNone()
    {
        var message = await Compose(Succeeded());

        message.Subject.ShouldBe("My Hub: Extract archive completed");
        message.TextBody.ShouldBe(
            """
            Hello jane,

            Your task "Extract archive" on acme/site-a completed.

            Duration: 45 s

            https://hub.example.org/r/acme/site-a?tab=tasks

            To change which task e-mails you get, open your notification settings: https://hub.example.org/r?preferences=notifications

            -- 
            My Hub
            https://hub.example.org/

            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task TheHtmlPart_HasTheButtonToTheTasksTab_TheReason_AndTheOptOutLink()
    {
        var html = (await Compose(Failed())).HtmlBody.ShouldNotBeNull();

        html.ShouldSatisfyAllConditions(
            h => h.ShouldContain("<title>My Hub: Photogrammetry (NodeODX) failed</title>"),
            h => h.ShouldContain("Hello jane,"),
            h => h.ShouldContain("Reason: The processing node ran out of memory"),
            h => h.ShouldContain("Duration: 17 min 4 s"),
            h => h.ShouldContain("href=\"https://hub.example.org/r/acme/site-a?tab=tasks\""),
            h => h.ShouldContain(">Open the tasks of the dataset</a>"),
            h => h.ShouldContain("href=\"https://hub.example.org/r?preferences=notifications\""));
    }

    [Test]
    public async Task EveryValue_IsEncoded_InTheHtml_AndAnErrorIsOneLine()
    {
        var message = await Compose(Failed("cannot read <broken.tif>\nsecond line"));

        message.HtmlBody.ShouldNotBeNull().ShouldSatisfyAllConditions(
            h => h.ShouldContain("cannot read &lt;broken.tif&gt; second line"),
            h => h.ShouldNotContain("<broken.tif>"));
        message.TextBody.ShouldContain("Reason: cannot read <broken.tif> second line");
    }

    [Test]
    public async Task WithoutAPublicUrl_NoLinkIsWritten_AndTheWordsAroundItAreLeftOut()
    {
        var message = await Compose(Failed(), externalUrl: null);

        message.TextBody.ShouldSatisfyAllConditions(
            t => t.ShouldNotContain("http"),
            t => t.ShouldNotContain("notification settings"),
            t => t.ShouldContain("Reason: The processing node ran out of memory"));
    }

    [Test]
    public async Task AnAdministratorsText_UsesTheSamePlaceholders()
    {
        var text = Text(new EmailTemplateText("{{result}}: {{label}}", "{{user}}, {{task}} ({{dataset}}) {{result}} after {{duration}}: {{error}}"));

        var message = await Compose(Failed(), text);

        message.Subject.ShouldBe("failed: flight-1/*.jpg");
        message.TextBody.ShouldStartWith(
            "jane, Photogrammetry (NodeODX) (acme/site-a) failed after 17 min 4 s: The processing node ran out of memory");
    }

    [Test]
    public void TheErrorPlaceholder_CannotBeUsedInTheSubject()
    {
        var ex = Should.Throw<PlaceholderTemplateException>(() =>
            Text(new EmailTemplateText("{{error}}", "Hi {{user}}")));

        ex.Message.ShouldContain("{{error}}");
    }

    [TestCase(0, "0 s")]
    [TestCase(45, "45 s")]
    [TestCase(59.6, "1 min 0 s")]
    [TestCase(17 * 60 + 4, "17 min 4 s")]
    [TestCase(3600, "1 h 0 min")]
    [TestCase(2 * 3600 + 5 * 60 + 30, "2 h 5 min")]
    [TestCase(-5, "0 s")]
    public void TheDuration_IsInWords(double seconds, string expected) =>
        TaskNotificationModel.Format(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);

    [Test]
    public async Task ThePreviewSample_ComposesThroughTheRealLayout()
    {
        var message = await TaskNotificationEmailTemplate.Instance.ComposeSampleAsync(Composer(), Text(), "admin", [Jane]);

        message.Subject.ShouldBe("My Hub: Photogrammetry (NodeODX) failed");
        message.TextBody.ShouldContain("Duration: 17 min 4 s");
    }


    [Test]
    public async Task AFailedTask_ThatCreatedItsDataset_LinksToTheOrganization_AndSaysTheDatasetIsRemoved()
    {
        var message = await Compose(Failed() with { DatasetRemoved = true });

        message.TextBody.ShouldContain("The dataset this run created, acme/site-a, is removed because the task failed.");
        message.TextBody.ShouldContain("https://hub.example.org/r/acme");
        message.TextBody.ShouldNotContain("tab=tasks");
        message.HtmlBody.ShouldNotBeNull().ShouldContain("href=\"https://hub.example.org/r/acme\"");
        message.HtmlBody.ShouldContain("Open the organization");
        message.HtmlBody.ShouldNotContain("tab=tasks");
    }

    [Test]
    public async Task AFailedTask_OfAnExistingDataset_KeepsTheTasksLink_AndNoNote()
    {
        var message = await Compose(Failed());

        message.TextBody.ShouldContain("https://hub.example.org/r/acme/site-a?tab=tasks");
        message.TextBody.ShouldNotContain("is removed");
    }

    [Test]
    public async Task ALabelWithLineBreaks_CannotBreakTheSubject_OrAHeader()
    {
        var text = Text(new EmailTemplateText("{{appName}}: {{task}} {{label}} {{result}}", "Files: {{label}}"));

        var message = await Compose(Failed() with { Label = "a.jpg\r\nBcc: evil@example.com\n\u2028b.jpg" }, text);

        message.Subject.ShouldBe("My Hub: Photogrammetry (NodeODX) a.jpg Bcc: evil@example.com b.jpg failed");
        message.Subject.ShouldNotContain("\r");
        message.Subject.ShouldNotContain("\n");
        message.TextBody.ShouldStartWith("Files: a.jpg Bcc: evil@example.com b.jpg");
    }
}
