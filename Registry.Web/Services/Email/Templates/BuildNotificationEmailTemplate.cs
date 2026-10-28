#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Registry.Web.Models.Email;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Hub;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Email.Templates;

/// <summary>What the build notification e-mail renders: the builds, and the text it is written with (custom or built-in).</summary>
/// <param name="Data">The user and the builds.</param>
/// <param name="Text">The subject and body.</param>
public sealed record BuildNotificationEmailModel(BuildNotificationModel Data, ParsedEmailTemplate Text);

/// <summary>
/// The values of the build notification placeholders for one e-mail. Both parts read them here, so the text and the HTML
/// say the same; links exist only with a public URL, and only as http(s) addresses.
/// </summary>
public sealed class BuildNotificationValues
{
    public const string AppName = "appName";
    public const string User = "user";
    public const string Summary = "summary";
    public const string FailedCount = "failedCount";
    public const string WaitingCount = "waitingCount";
    public const string CompletedCount = "completedCount";
    public const string Datasets = "datasets";
    public const string Builds = "builds";
    public const string Link = "link";
    public const string PreferencesLink = "preferencesLink";

    /// <summary>The placeholders of the build notification e-mail.</summary>
    public static readonly IReadOnlyList<EmailPlaceholder> Placeholders =
    [
        new(AppName, "The application name", InSubject: true),
        new(User, "The name of the user", InSubject: true),
        new(Summary, "The counts in words, e.g. \"2 builds failed, 1 waiting for files, 3 completed\"", InSubject: true),
        new(FailedCount, "The number of failed builds", InSubject: true),
        new(WaitingCount, "The number of builds waiting for files that are not in the dataset", InSubject: true),
        new(CompletedCount, "The number of completed builds", InSubject: true),
        new(Datasets, "The datasets of the builds (org/dataset, comma separated)", InSubject: true),
        new(Builds, "The list of the builds, with the reason of each failure and the files each waiting build needs", InSubject: false, IsBlock: true),
        new(Link, "A link to the dataset (to the Hub when the builds are in several datasets); alone in its paragraph it is a button", InSubject: false),
        new(PreferencesLink, "A link to the user's notification settings, to change or stop these e-mails", InSubject: false)
    ];

    private readonly BuildNotificationModel _model;
    private readonly EmailBranding _branding;

    public BuildNotificationValues(BuildNotificationModel model, EmailBranding branding)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _branding = branding ?? throw new ArgumentNullException(nameof(branding));

        var datasets = model.Ordered.Select(b => (b.OrgSlug, b.DsSlug)).Distinct().ToList();
        if (datasets.Count == 1)
        {
            LinkUrl = HubLinks.Dataset(branding.BaseUrl, datasets[0].OrgSlug, datasets[0].DsSlug);
            LinkLabel = "Open the dataset";
        }
        else
        {
            LinkUrl = branding.BaseUrl;
            LinkLabel = $"Open {branding.AppName}";
        }

        PreferencesUrl = HubLinks.NotificationPreferences(branding.BaseUrl);
    }

    /// <summary>Where <c>{{link}}</c> goes; null without a public URL.</summary>
    public Uri? LinkUrl { get; }

    /// <summary>The label of <c>{{link}}</c> as a button.</summary>
    public string LinkLabel { get; }

    /// <summary>Where <c>{{preferencesLink}}</c> goes; null without a public URL.</summary>
    public Uri? PreferencesUrl { get; }

    /// <summary>The builds, failures first.</summary>
    public IReadOnlyList<BuildNotificationLine> Lines => _model.Ordered;

    /// <summary>The dataset page of <paramref name="line"/>; null without a public URL.</summary>
    public Uri? DatasetUrl(BuildNotificationLine line) => HubLinks.Dataset(_branding.BaseUrl, line.OrgSlug, line.DsSlug);

    /// <summary>The address a link placeholder stands for; null for another placeholder or without a public URL.</summary>
    public Uri? LinkOf(string name) => name switch
    {
        Link => LinkUrl,
        PreferencesLink => PreferencesUrl,
        _ => null
    };

    /// <summary>Whether <paramref name="name"/> is a link placeholder.</summary>
    public static bool IsLink(string name) => name is Link or PreferencesLink;

    /// <summary>Whether the placeholder has a value (a link has none without a public URL).</summary>
    public bool Has(string name) => Text(name) != null;

    /// <summary>The value as plain text (a link is its address, the builds a bulleted list); null when it has none.</summary>
    public string? Text(string name) => name switch
    {
        AppName => _branding.AppName,
        User => _model.UserName,
        Summary => _model.Summary,
        FailedCount => _model.FailedCount.ToString(CultureInfo.InvariantCulture),
        WaitingCount => _model.WaitingCount.ToString(CultureInfo.InvariantCulture),
        CompletedCount => _model.CompletedCount.ToString(CultureInfo.InvariantCulture),
        Datasets => string.Join(", ", _model.Datasets),
        Builds => BuildList(),
        Link or PreferencesLink => LinkOf(name)?.ToString(),
        _ => null
    };

    /// <summary>What a build's outcome is called.</summary>
    public static string OutcomeText(BuildOutcome outcome) => outcome switch
    {
        BuildOutcome.Failed => "Failed",
        BuildOutcome.WaitingForFiles => "Waiting for files",
        _ => "Completed"
    };

    /// <summary>The fixed colour of a build's outcome.</summary>
    public static string OutcomeColor(BuildOutcome outcome) => outcome switch
    {
        BuildOutcome.Failed => EmailStyles.Failed,
        BuildOutcome.WaitingForFiles => EmailStyles.Waiting,
        _ => EmailStyles.Completed
    };

    /// <summary>
    /// What a build's line says under its file, as both parts show it: the reason of a failure, the files a waiting build
    /// needs; null when it says nothing.
    /// </summary>
    public static string? Detail(BuildNotificationLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (string.IsNullOrWhiteSpace(line.Error))
            return null;

        var text = line.Error.Trim().ReplaceLineEndings(" ");
        return line.Outcome == BuildOutcome.WaitingForFiles ? $"Missing: {text}" : text;
    }

    private string BuildList()
    {
        var text = new StringBuilder();
        foreach (var line in Lines)
        {
            text.Append("- ").Append(OutcomeText(line.Outcome)).Append(": ").Append(line.Path)
                .Append(" (").Append(line.Dataset).Append(')').Append('\n');
            if (Detail(line) is { } detail)
                text.Append(line.Outcome == BuildOutcome.Failed ? "  Reason: " : "  ").Append(detail).Append('\n');
        }

        return text.ToString();
    }
}

/// <summary>
/// The e-mail that tells a user that builds of theirs completed, failed or wait for files. Administrators can change its text
/// (<see cref="IEditableEmailTemplate"/>); the built-in text below is placeholder text too.
/// </summary>
public sealed class BuildNotificationEmailTemplate : EmailTemplate<BuildNotificationEmailModel, BuildNotificationEmail>,
    IEditableEmailTemplate
{
    /// <summary>The name of the template in the API and in the template file.</summary>
    public const string TemplateName = "build-notification";

    /// <summary>The template (it has no state).</summary>
    public static readonly BuildNotificationEmailTemplate Instance = new();

    private static readonly EmailTemplateText BuiltIn = new(
        "{{appName}}: {{summary}}",
        """
        Hello {{user}},

        {{summary}} on {{appName}}.

        {{builds}}

        {{link}}

        To change which build e-mails you get, open your notification settings: {{preferencesLink}}
        """);

    private BuildNotificationEmailTemplate()
    {
    }

    /// <inheritdoc />
    public string Name => TemplateName;

    /// <inheritdoc />
    public string Title => "Build notifications";

    /// <inheritdoc />
    public EmailTemplateText BuiltInText => BuiltIn;

    /// <inheritdoc />
    public IReadOnlyList<EmailPlaceholder> Placeholders => BuildNotificationValues.Placeholders;

    /// <inheritdoc />
    public override string Subject(BuildNotificationEmailModel model, EmailBranding branding) =>
        model.Text.Subject.RenderLine(new BuildNotificationValues(model.Data, branding).Text);

    /// <inheritdoc />
    public override string Text(BuildNotificationEmailModel model, EmailBranding branding) =>
        model.Text.Body.RenderText(new BuildNotificationValues(model.Data, branding).Text);

    /// <inheritdoc />
    public Task<EmailMessage> ComposeSampleAsync(IEmailComposer composer, ParsedEmailTemplate text, string userName,
        IReadOnlyList<EmailAddress> recipients)
    {
        ArgumentNullException.ThrowIfNull(composer);

        return composer.ComposeAsync(this, new BuildNotificationEmailModel(Sample(userName), text), recipients);
    }

    /// <summary>The builds the preview and the test e-mail show: one that completed, one that failed, one that waits for files.</summary>
    public static BuildNotificationModel Sample(string userName)
    {
        var now = DateTime.UtcNow;
        return new BuildNotificationModel(userName,
        [
            new BuildNotificationLine("demo", "survey-2026", "flight-1/orthophoto.tif", BuildOutcome.Completed, null, now.AddMinutes(-3)),
            new BuildNotificationLine("demo", "survey-2026", "flight-2/cloud.laz", BuildOutcome.Failed,
                "The point cloud has no points", now.AddMinutes(-1)),
            new BuildNotificationLine("demo", "survey-2026", "flight-3/model.obj", BuildOutcome.WaitingForFiles,
                "flight-3/model.mtl, flight-3/texture.png", now.AddMinutes(-2))
        ]);
    }
}
