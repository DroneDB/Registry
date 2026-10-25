#nullable enable
using System;

namespace Registry.Web.Services.Email.Templates;

/// <summary>What the update e-mail shows.</summary>
/// <param name="InstalledVersion">The installed Registry version; null when it is not known.</param>
/// <param name="LatestVersion">The latest published version.</param>
/// <param name="IsSecurityUpdate">Whether a release between the two has a security section in its notes.</param>
/// <param name="ReleaseNotes">
/// The notes of the releases in between, merged into one Markdown document (the update check's merge). Untrusted text from
/// the update check service: it is shown as text and never rendered as Markdown or HTML.
/// </param>
/// <param name="CompareUrl">The page that compares the installed version with the latest; null when there is none.</param>
public sealed record UpdateAvailableEmailModel(string? InstalledVersion, string LatestVersion, bool IsSecurityUpdate,
    string? ReleaseNotes, string? CompareUrl)
{
    /// <summary>The installed version as the e-mail says it.</summary>
    public string InstalledVersionText => string.IsNullOrWhiteSpace(InstalledVersion) ? "unknown" : InstalledVersion;

    /// <summary>
    /// <see cref="CompareUrl"/> when it is an absolute http or https address, otherwise null: the address comes from the
    /// update check service and is put in a link, where a <c>javascript:</c> address must not end up.
    /// </summary>
    public Uri? CompareLink =>
        Uri.TryCreate(CompareUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
}

/// <summary>
/// The e-mail that tells the administrators a newer Registry is available (or that it carries security fixes). The
/// notes are text in both parts, so the server does not render the untrusted Markdown.
/// </summary>
public sealed class UpdateAvailableEmailTemplate : EmailTemplate<UpdateAvailableEmailModel, UpdateAvailableEmail>
{
    /// <summary>The configuration key that switches this e-mail off or limits it to security updates.</summary>
    public const string OptOutKey = "AppSettings:UpdateCheckEmail";

    /// <summary>The template (it has no state).</summary>
    public static readonly UpdateAvailableEmailTemplate Instance = new();

    private UpdateAvailableEmailTemplate()
    {
    }

    /// <inheritdoc />
    public override string Subject(UpdateAvailableEmailModel model, EmailBranding branding) =>
        model.IsSecurityUpdate ? $"Registry {model.LatestVersion}: security update" : $"Registry {model.LatestVersion} is available";

    /// <inheritdoc />
    public override string Text(UpdateAvailableEmailModel model, EmailBranding branding)
    {
        var text = new System.Text.StringBuilder();

        text.Append(model.IsSecurityUpdate
            ? "A new version of Registry is available, and it includes security fixes."
            : "A new version of Registry is available.");
        text.Append("\n\n");
        text.Append("Installed version: ").Append(model.InstalledVersionText).Append('\n');
        text.Append("Latest version: ").Append(model.LatestVersion).Append('\n');

        if (!string.IsNullOrWhiteSpace(model.ReleaseNotes))
            text.Append("\nWhat changed:\n\n").Append(model.ReleaseNotes.Trim()).Append('\n');

        if (branding.BaseUrl != null)
            text.Append("\nOpen ").Append(branding.AppName).Append(": ").Append(branding.BaseUrl).Append('\n');

        text.Append("\nYou get this e-mail because ").Append(OptOutKey).Append(" is \"security\" or \"all\". Set it to \"off\" to stop it.");
        return text.ToString();
    }
}
