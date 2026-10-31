using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.Email;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Email;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Test.Support;
using Registry.Web.Test.UpdateCheck;
using Shouldly;

namespace Registry.Web.Test.Email;

/// <summary>
/// The build notification e-mail composed with the real Razor renderer, from the built-in text and from texts an
/// administrator could write: both parts, the escaping of every value, the links, and the composer that reads the
/// saved text.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~BuildNotificationEmailTests"
/// </summary>
[TestFixture]
public class BuildNotificationEmailTests
{
    private const string BaseUrl = "https://hub.example.org";
    private static readonly EmailAddress Jane = new("jane@example.com", "jane");
    private static readonly DateTime Now = new(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);

    private static EmailComposer Composer(string? externalUrl = BaseUrl) =>
        new(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(new HubOptions { AppName = "My Hub" }),
            Microsoft.Extensions.Options.Options.Create(new HttpHostOptions { ExternalUrlOverride = externalUrl }));

    private static BuildNotificationModel Model(string error = "Out of memory", string ds = "site-a") =>
        new("jane",
        [
            new BuildNotificationLine("acme", ds, "ortho.tif", BuildOutcome.Completed, null, Now),
            new BuildNotificationLine("acme", ds, "cloud.laz", BuildOutcome.Failed, error, Now.AddMinutes(1))
        ]);

    private static ParsedEmailTemplate Text(EmailTemplateText? text = null) =>
        ParsedEmailTemplate.Parse(BuildNotificationEmailTemplate.Instance, text ?? BuildNotificationEmailTemplate.Instance.BuiltInText);

    private static Task<EmailMessage> Compose(BuildNotificationModel model, ParsedEmailTemplate? text = null, string? externalUrl = BaseUrl) =>
        Composer(externalUrl).ComposeAsync(BuildNotificationEmailTemplate.Instance,
            new BuildNotificationEmailModel(model, text ?? Text()), [Jane]);

    [Test]
    public async Task BuiltInText_SubjectAndTextPart()
    {
        var message = await Compose(Model());

        message.Subject.ShouldBe("My Hub: 1 build failed, 1 completed");
        message.TextBody.ShouldBe(
            """
            Hello jane,

            1 build failed, 1 completed on My Hub.

            - Failed: cloud.laz (acme/site-a)
              Reason: Out of memory
            - Completed: ortho.tif (acme/site-a)

            https://hub.example.org/r/acme/site-a

            To change which build e-mails you get, open your notification settings: https://hub.example.org/r?preferences=notifications

            -- 
            My Hub
            https://hub.example.org/

            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task BuiltInText_HtmlPart_HasTheTable_TheButton_AndTheOptOutLink()
    {
        var html = (await Compose(Model())).HtmlBody.ShouldNotBeNull();

        html.ShouldSatisfyAllConditions(
            h => h.ShouldContain("<title>My Hub: 1 build failed, 1 completed</title>"),
            h => h.ShouldContain("Hello jane,"),
            h => h.ShouldContain(">Dataset</th>"),
            h => h.ShouldContain("cloud.laz"),
            h => h.ShouldContain("Out of memory"),
            // Fixed semantic colours, not the accent
            h => h.ShouldContain($"color:{EmailStyles.Failed};"),
            h => h.ShouldContain($"color:{EmailStyles.Completed};"),
            // One dataset: the button opens it, in the accent with readable text
            h => h.ShouldContain($"bgcolor=\"{EmailStyles.BrandBlue}\""),
            h => h.ShouldMatch("href=\"https://hub.example.org/r/acme/site-a\" style=\"display:inline-block;[^\"]*color:#ffffff;"),
            h => h.ShouldContain(">Open the dataset</a>"),
            h => h.ShouldContain("href=\"https://hub.example.org/r?preferences=notifications\""));
    }

    private static BuildNotificationModel WithAWait() => Model() with
    {
        Builds =
        [
            .. Model().Builds,
            new BuildNotificationLine("acme", "site-a", "model.obj", BuildOutcome.WaitingForFiles,
                "model.mtl, textures/wall.png", Now.AddMinutes(2))
        ]
    };

    [Test]
    public async Task ABuildWaitingForFiles_IsBetweenTheFailuresAndTheCompletions_WithTheFilesItNeeds()
    {
        var message = await Compose(WithAWait(), Text(new EmailTemplateText("{{summary}} ({{waitingCount}} waiting)", "{{builds}}")));

        message.Subject.ShouldBe("1 build failed, 1 waiting for files, 1 completed (1 waiting)");
        message.TextBody.ShouldStartWith(
            """
            - Failed: cloud.laz (acme/site-a)
              Reason: Out of memory
            - Waiting for files: model.obj (acme/site-a)
              Missing: model.mtl, textures/wall.png
            - Completed: ortho.tif (acme/site-a)

            """.ReplaceLineEndings("\n"));
        message.HtmlBody.ShouldNotBeNull().ShouldSatisfyAllConditions(
            h => h.ShouldContain("Missing: model.mtl, textures/wall.png"),
            // The Hub's warning, yellow with dark text: the result on the yellow, the files in the dark text, never orange
            h => h.ShouldContain($"background-color:{EmailStyles.WaitingBackground};color:{EmailStyles.Waiting};\">Waiting for files</span>"),
            h => h.ShouldContain($"color:{EmailStyles.Waiting};word-break:break-word;\">Missing: model.mtl"),
            h => h.ShouldNotContain("#b45309"),
            // The other results keep their text colour on the white, without the yellow
            h => Regex.Count(h, $"background-color:{EmailStyles.WaitingBackground}").ShouldBe(1));
    }

    [Test]
    public void AWaitingBuild_IsTheHubsWarning_DarkTextOnYellow()
    {
        // The warn tag of the Hub's theme: Lara's yellow.950 on yellow.400
        var theme = RepositoryFiles.PathOf("Registry.Web/ClientApp/webapp/js/theme.js");
        if (!File.Exists(theme))
            Assert.Ignore("The Hub submodule is not checked out");
        File.ReadAllText(theme).ShouldContain("const warnTag = { background: '{yellow.400}', color: '{yellow.950}' };");
        (EmailStyles.WaitingBackground, EmailStyles.Waiting).ShouldBe(("#facc15", "#422006"));

        EmailStyles.TextOn(EmailStyles.WaitingBackground).ShouldBe(EmailStyles.DarkText);
        EmailStyles.ReadableOnWhite(EmailStyles.Waiting).ShouldBe(EmailStyles.Waiting);
    }

    [Test]
    public async Task OnlyWaits_TheSummaryCountsThem()
    {
        var model = new BuildNotificationModel("jane",
        [
            new BuildNotificationLine("acme", "site-a", "a.obj", BuildOutcome.WaitingForFiles, "a.mtl", Now),
            new BuildNotificationLine("acme", "site-a", "b.shp", BuildOutcome.WaitingForFiles, "b.shx", Now)
        ]);

        (await Compose(model)).Subject.ShouldBe("My Hub: 2 builds waiting for files");
    }

    [Test]
    public void TheSample_ShowsEveryKind()
    {
        BuildNotificationEmailTemplate.Sample("jane").Builds.Select(b => b.Outcome).Distinct()
            .ShouldBe(Enum.GetValues<BuildOutcome>(), ignoreOrder: true);
    }

    [Test]
    public async Task SeveralDatasets_TheLinkIsTheHub()
    {
        var model = Model() with
        {
            Builds = [.. Model().Builds, new BuildNotificationLine("acme", "site-b", "dem.tif", BuildOutcome.Completed, null, Now)]
        };

        var message = await Compose(model);

        message.HtmlBody.ShouldNotBeNull().ShouldContain(">Open My Hub</a>");
        message.TextBody.ShouldContain("\n\nhttps://hub.example.org/\n\n");
        message.Subject.ShouldBe("My Hub: 1 build failed, 2 completed");
    }

    [Test]
    public async Task WithoutAPublicUrl_ThereIsNoLink_AndTheParagraphsAroundTheLinksAreLeftOut()
    {
        var message = await Compose(Model(), externalUrl: null);

        message.HtmlBody.ShouldNotBeNull().ShouldNotContain("href=");
        message.HtmlBody.ShouldNotContain("notification settings");
        message.HtmlBody.ShouldContain("acme/site-a");
        message.TextBody.ShouldNotContain("http");
        message.TextBody.ShouldNotContain("notification settings");
        message.TextBody.ShouldContain("- Failed: cloud.laz (acme/site-a)");
    }

    [Test]
    public async Task AdministratorText_IsEncoded_InTheHtmlPart()
    {
        var text = Text(new EmailTemplateText("<b>{{summary}}</b>",
            "<script>alert(1)</script> <a href=\"javascript:alert(2)\">x</a> {{user}}\n\n<img src=x onerror=alert(3)>"));

        var message = await Compose(Model(), text);

        message.HtmlBody.ShouldNotBeNull().ShouldSatisfyAllConditions(
            h => h.ShouldNotContain("<script"),
            h => h.ShouldNotContain("<img src=x"),
            h => h.ShouldNotContain("href=\"javascript:"),
            h => h.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;"),
            h => h.ShouldContain("&lt;img src=x onerror=alert(3)&gt;"),
            h => h.ShouldContain("<title>&lt;b&gt;1 build failed, 1 completed&lt;/b&gt;</title>"));
        // The plain part and the subject are text already
        message.Subject.ShouldBe("<b>1 build failed, 1 completed</b>");
        message.TextBody.ShouldStartWith("<script>alert(1)</script>");
    }

    [Test]
    public async Task BuildErrorsAndNames_AreEncoded_AndNeverLinks()
    {
        var model = Model(error: "<script>steal()</script> javascript:alert(1)", ds: "site-a") with { UserName = "<i>jane</i>" };

        var html = (await Compose(model)).HtmlBody.ShouldNotBeNull();

        html.ShouldNotContain("<script>steal()");
        html.ShouldContain("&lt;script&gt;steal()&lt;/script&gt; javascript:alert(1)");
        html.ShouldNotContain("<i>jane</i>");
        html.ShouldNotContain("href=\"javascript:");
    }

    [Test]
    public async Task TheBuiltInText_AndTheSameTextSavedByAnAdministrator_RenderTheSame()
    {
        var builtIn = BuildNotificationEmailTemplate.Instance.BuiltInText;

        var asDefault = await Compose(Model());
        var asCustom = await Compose(Model(), Text(builtIn with { }));

        asCustom.ShouldBe(asDefault);
    }

    [Test]
    public async Task ALinkInsideText_IsAnAnchorShowingTheAddress_NotAButton()
    {
        var html = (await Compose(Model(), Text(new EmailTemplateText("s", "See {{link}} or {{preferencesLink}}")))).HtmlBody!;

        html.ShouldContain(">https://hub.example.org/r/acme/site-a</a>");
        html.ShouldNotContain("display:inline-block");
    }

    [Test]
    public async Task Composer_UsesTheSavedText_AndFallsBackToTheBuiltInOne_WhenItIsNoLongerValid()
    {
        using var storage = new TempStorage();
        var store = new EditableEmailTemplateStore(storage.Storage, UpdateCheckTestKit.Disk, NullLogger<EditableEmailTemplateStore>.Instance);
        var templates = new EditableEmailTemplates([BuildNotificationEmailTemplate.Instance], store, NullLogger<EditableEmailTemplates>.Instance);
        var composer = new BuildNotificationComposer(Composer(), templates);

        (await composer.ComposeAsync(Jane, Model())).Subject.ShouldBe("My Hub: 1 build failed, 1 completed");

        store.Set(BuildNotificationEmailTemplate.TemplateName, new EmailTemplateText("{{failedCount}} failed", "Hi {{user}}"));
        var custom = await composer.ComposeAsync(Jane, Model());
        custom.Subject.ShouldBe("1 failed");
        custom.To.ShouldHaveSingleItem().ShouldBe(Jane);

        // A text saved by an older version with a placeholder this one does not know
        store.Set(BuildNotificationEmailTemplate.TemplateName, new EmailTemplateText("{{gone}}", "Hi"));
        (await composer.ComposeAsync(Jane, Model())).Subject.ShouldBe("My Hub: 1 build failed, 1 completed");
        templates.Current(BuildNotificationEmailTemplate.Instance).IsDefault.ShouldBeTrue();

        store.Remove(BuildNotificationEmailTemplate.TemplateName);
        store.Get(BuildNotificationEmailTemplate.TemplateName).ShouldBeNull();
    }

    [TestCase("subject", null, TestName = "A saved text without a subject is read back as the built-in text, with a warning")]
    [TestCase("body", null, TestName = "A saved text without a body is read back as the built-in text, with a warning")]
    [TestCase("subject", ParsedEmailTemplate.MaxSubjectLength + 1, TestName = "A saved subject over the limit is read back as the built-in text, with a warning")]
    [TestCase("body", ParsedEmailTemplate.MaxBodyLength + 1, TestName = "A saved body over the limit is read back as the built-in text, with a warning")]
    public void ASavedFileWithAFieldMissingOrTooLong_IsReadAsTheBuiltInText_WithAWarning(string field, int? length)
    {
        // The file is shared by every node and can be edited by hand, or written by another version
        using var storage = new TempStorage();
        var store = new EditableEmailTemplateStore(storage.Storage, UpdateCheckTestKit.Disk, NullLogger<EditableEmailTemplateStore>.Instance);
        store.Set(BuildNotificationEmailTemplate.TemplateName, new EmailTemplateText("Builds", "Hi {{user}}"));
        var path = storage.File(MagicStrings.EmailTemplatesFileName);
        var file = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path))!;
        var saved = file[BuildNotificationEmailTemplate.TemplateName]!.AsObject();
        var key = saved.Select(p => p.Key).Single(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase));
        saved[key] = length == null ? null : new string('x', length.Value);
        System.IO.File.WriteAllText(path, file.ToJsonString());
        var logs = new RecordingLoggerFactory();
        var templates = new EditableEmailTemplates([BuildNotificationEmailTemplate.Instance], store,
            logs.CreateLogger<EditableEmailTemplates>());

        var current = templates.Current(BuildNotificationEmailTemplate.Instance);

        current.IsDefault.ShouldBeTrue();
        current.Text.ShouldBe(BuildNotificationEmailTemplate.Instance.BuiltInText);
        logs.Entries.ShouldContain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                                        e.Message.Contains(BuildNotificationEmailTemplate.TemplateName) &&
                                        e.Message.Contains("the built-in text is used"));
    }

    [Test]
    public void Summary_CountsInWords()
    {
        var failed = new BuildNotificationLine("o", "d", "a", BuildOutcome.Failed, "x", Now);
        var done = failed with { Outcome = BuildOutcome.Completed, Error = null };

        new BuildNotificationModel("u", [failed]).Summary.ShouldBe("1 build failed");
        new BuildNotificationModel("u", [failed, failed]).Summary.ShouldBe("2 builds failed");
        new BuildNotificationModel("u", [done, done, done]).Summary.ShouldBe("3 builds completed");
        new BuildNotificationModel("u", [failed, failed, done, done, done]).Summary.ShouldBe("2 builds failed, 3 completed");
        Regex.IsMatch(new BuildNotificationModel("u", [done, failed]).Ordered[0].Outcome.ToString(), "Failed").ShouldBeTrue();
    }
}
