#nullable enable
using System;
using System.Linq;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// Turns a remembered update check result into what the Hub and the update e-mail show: the comparison of the versions,
/// the merged release notes and the link to request a licence. It is the only place that builds that link.
/// </summary>
public sealed class UpdateInfoBuilder
{
    /// <summary>The licence request form used when the update check service names none (or not an HTTPS one).</summary>
    public const string DefaultLicenseRequestUrl = "https://dronedb.app/request-license";

    private readonly IInstanceIdentity _identity;
    private readonly UpdateCheckSettingsStore _settings;
    private readonly InstanceBaseUrl _baseUrl;

    public UpdateInfoBuilder(IInstanceIdentity identity, UpdateCheckSettingsStore settings, InstanceBaseUrl baseUrl)
    {
        _identity = identity;
        _settings = settings;
        _baseUrl = baseUrl;
    }

    /// <summary>Whether <paramref name="latest"/> is a newer version than <paramref name="current"/>; false when either is not a version.</summary>
    public static bool IsNewer(string? latest, string? current) =>
        Version.TryParse(latest, out var latestVersion) && Version.TryParse(current, out var currentVersion) &&
        latestVersion > currentVersion;

    /// <summary>The information for <paramref name="result"/>, the last good answer (null: none yet).</summary>
    public UpdateInfoDto Build(UpdateCheckResult? result)
    {
        var identity = _identity.Current;
        var settings = _settings.Read();
        var current = RegistryVersion.Current;

        var info = new UpdateInfoDto
        {
            InstanceId = identity.Id,
            CurrentVersion = current,
            UseDeclaration = settings.UseDeclaration,
            ContactEmail = settings.ConsentedEmail,
            LicenseRequestUrl = LicenseRequestUrl(result?.Response.LicenseRequestUrl, identity.Id, current)
        };

        if (result is null)
            return info;

        var response = result.Response;
        info.LatestVersion = response.LatestVersion;
        info.ReleaseDate = response.ReleaseDate;
        info.ChangelogUrl = response.ChangelogUrl;
        info.CompareUrl = response.CompareUrl;
        info.LastChecked = result.CheckedAt;
        info.IsUpdateAvailable = IsNewer(response.LatestVersion, current);

        // The range was cut at the version that asked: after an upgrade the older releases are no news
        var news = info.IsUpdateAvailable ? response.Releases.Where(r => IsNewer(r.Version, current)).ToArray() : [];
        var notes = ReleaseNotesMerger.Merge(news, response.CompareUrl, response.ReleasesTruncated);
        info.ReleaseNotes = notes.Markdown;
        info.IsSecurityUpdate = notes.IsSecurityUpdate;
        return info;
    }

    // The values travel to a form of dronedb.app: each one escaped, the url empty when the deployment has none
    private string LicenseRequestUrl(string? configured, string instanceId, string? version)
    {
        var form = configured ?? DefaultLicenseRequestUrl;
        var separator = form.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{form}{separator}instance={Uri.EscapeDataString(instanceId)}" +
               $"&url={Uri.EscapeDataString(_baseUrl.Current() ?? string.Empty)}" +
               $"&version={Uri.EscapeDataString(version ?? string.Empty)}";
    }
}
