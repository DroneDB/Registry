using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Registry.Web.Models.Email;
using Registry.Web.Services.Hub;
using Registry.Web.Services.Ports;
using Registry.Web.Services.TaskNotifications;

namespace Registry.Web.Services.Email.Templates;

/// <summary>What the task notification e-mail renders: the task, and the text it is written with (custom or built-in).</summary>
/// <param name="Data">The user and the task.</param>
/// <param name="Text">The subject and body.</param>
public sealed record TaskNotificationEmailModel(TaskNotificationModel Data, ParsedEmailTemplate Text);

/// <summary>
/// The values of the task notification placeholders for one e-mail. Both parts read them here, so the text and the HTML
/// say the same; links exist only with a public URL, and only as http(s) addresses. A value the task does not have (the
/// reason of a task that succeeded, the label of a task without files) is none: the paragraph that uses it is left out.
/// </summary>
public sealed class TaskNotificationValues
{
    public const string AppName = "appName";
    public const string User = "user";
    public const string Task = "task";
    public const string Label = "label";
    public const string Dataset = "dataset";
    public const string Result = "result";
    public const string Error = "error";
    public const string Duration = "duration";
    public const string DatasetNote = "datasetNote";
    public const string Link = "link";
    public const string PreferencesLink = "preferencesLink";

    /// <summary>The placeholders of the task notification e-mail.</summary>
    public static readonly IReadOnlyList<EmailPlaceholder> Placeholders =
    [
        new(AppName, "The application name", InSubject: true),
        new(User, "The name of the user", InSubject: true),
        new(Task, "What the task is, e.g. \"Photogrammetry (NodeODX)\"", InSubject: true),
        new(Label, "The files the task works on, as the task list names them (a paragraph that uses it is left out for a task without)", InSubject: true),
        new(Dataset, "The dataset of the task (org/dataset)", InSubject: true),
        new(Result, "How the task ended: completed or failed", InSubject: true),
        new(Error, "Why the task failed (a paragraph that uses it is left out for a task that completed)", InSubject: false),
        new(Duration, "How long the task ran, e.g. \"17 min 4 s\"", InSubject: true),
        new(DatasetNote, "Says that the dataset this run created is removed (a paragraph that uses it is left out when the task did not create the dataset or did not fail)", InSubject: false),
        new(Link, "A link to the Tasks tab of the dataset, or to the organization when the dataset is removed; alone in its paragraph it is a button", InSubject: false),
        new(PreferencesLink, "A link to the user's notification settings, to change or stop these e-mails", InSubject: false)
    ];

    private readonly TaskNotificationModel _model;

    public TaskNotificationValues(TaskNotificationModel model, EmailBranding branding)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentNullException.ThrowIfNull(branding);

        // The dataset a failed task created is deleted by its rollback: its Tasks tab would be a page that is gone
        LinkUrl = model.DatasetRemoved
            ? HubLinks.Organization(branding.BaseUrl, model.OrgSlug)
            : HubLinks.DatasetTasks(branding.BaseUrl, model.OrgSlug, model.DsSlug);
        LinkLabelText = model.DatasetRemoved ? "Open the organization" : "Open the tasks of the dataset";
        PreferencesUrl = HubLinks.NotificationPreferences(branding.BaseUrl);
        AppNameText = branding.AppName;
    }

    private string AppNameText { get; }

    private string LinkLabelText { get; }

    /// <summary>Where <c>{{link}}</c> goes; null without a public URL.</summary>
    public Uri? LinkUrl { get; }

    /// <summary>The label of <c>{{link}}</c> as a button.</summary>
    public string LinkLabel => LinkLabelText;

    /// <summary>Where <c>{{preferencesLink}}</c> goes; null without a public URL.</summary>
    public Uri? PreferencesUrl { get; }

    /// <summary>The address a link placeholder stands for; null for another placeholder or without a public URL.</summary>
    public Uri? LinkOf(string name) => name switch
    {
        Link => LinkUrl,
        PreferencesLink => PreferencesUrl,
        _ => null
    };

    /// <summary>Whether <paramref name="name"/> is a link placeholder.</summary>
    public static bool IsLink(string name) => name is Link or PreferencesLink;

    /// <summary>Whether the placeholder has a value.</summary>
    public bool Has(string name) => Text(name) != null;

    /// <summary>The value as plain text (a link is its address); null when it has none.</summary>
    public string? Text(string name) => name switch
    {
        AppName => AppNameText,
        User => OneLine(_model.UserName),
        Task => OneLine(_model.Task),
        Label => OneLine(NullIfBlank(_model.Label)),
        Dataset => _model.Dataset,
        Result => _model.Result,
        Error => _model.Succeeded ? null : OneLine(NullIfBlank(_model.Error)),
        DatasetNote => !_model.Succeeded && _model.DatasetRemoved
            ? $"The dataset this run created, {_model.Dataset}, is removed because the task failed."
            : null,
        Duration => _model.DurationText,
        Link or PreferencesLink => LinkOf(name)?.ToString(),
        _ => null
    };

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // A value that came from outside (a file name, a display name) never breaks a line of the subject or of a paragraph
    private static string? OneLine(string? text) => text == null ? null : LineBreaks.Replace(text, " ");

    private static readonly Regex LineBreaks = new(@"[\r\n\u0085\u2028\u2029]+", RegexOptions.CultureInvariant);
}

/// <summary>
/// The e-mail that tells a user that a task of theirs completed or failed (FR-PRC-40, D-012): the task, its files, why it
/// failed, how long it ran, and a link to the Tasks tab of the dataset. Administrators can change its text
/// (<see cref="IEditableEmailTemplate"/>); the built-in text below is placeholder text too.
/// </summary>
public sealed class TaskNotificationEmailTemplate : EmailTemplate<TaskNotificationEmailModel, TaskNotificationEmail>,
    IEditableEmailTemplate
{
    /// <summary>The name of the template in the API and in the template file.</summary>
    public const string TemplateName = "task-notification";

    /// <summary>The template (it has no state).</summary>
    public static readonly TaskNotificationEmailTemplate Instance = new();

    private static readonly EmailTemplateText BuiltIn = new(
        "{{appName}}: {{task}} {{result}}",
        """
        Hello {{user}},

        Your task "{{task}}" on {{dataset}} {{result}}.

        Files: {{label}}

        Reason: {{error}}

        {{datasetNote}}

        Duration: {{duration}}

        {{link}}

        To change which task e-mails you get, open your notification settings: {{preferencesLink}}
        """);

    private TaskNotificationEmailTemplate()
    {
    }

    /// <inheritdoc />
    public string Name => TemplateName;

    /// <inheritdoc />
    public string Title => "Task notifications";

    /// <inheritdoc />
    public EmailTemplateText BuiltInText => BuiltIn;

    /// <inheritdoc />
    public IReadOnlyList<EmailPlaceholder> Placeholders => TaskNotificationValues.Placeholders;

    /// <inheritdoc />
    public override string Subject(TaskNotificationEmailModel model, EmailBranding branding) =>
        model.Text.Subject.RenderLine(new TaskNotificationValues(model.Data, branding).Text);

    /// <inheritdoc />
    public override string Text(TaskNotificationEmailModel model, EmailBranding branding) =>
        model.Text.Body.RenderText(new TaskNotificationValues(model.Data, branding).Text);

    /// <inheritdoc />
    public Task<EmailMessage> ComposeSampleAsync(IEmailComposer composer, ParsedEmailTemplate text, string userName,
        IReadOnlyList<EmailAddress> recipients)
    {
        ArgumentNullException.ThrowIfNull(composer);

        return composer.ComposeAsync(this, new TaskNotificationEmailModel(Sample(userName), text), recipients);
    }

    /// <summary>The task the preview and the test e-mail show: a photogrammetry run that failed.</summary>
    public static TaskNotificationModel Sample(string userName) =>
        new(userName, "Photogrammetry (NodeODX)", "flight-1/*.jpg (126 images)", "demo", "survey-2026", Succeeded: false,
            "The processing node ran out of memory while it built the point cloud", TimeSpan.FromSeconds(17 * 60 + 4));
}
