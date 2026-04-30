namespace Registry.Web.Services.Hub;

/// <summary>
/// Static holder for the current Hub (Vue ClientApp) version installed on disk.
/// Populated once at startup by <c>Program.SetupHub()</c> and surfaced to clients
/// via <c>FeaturesDto.HubVersion</c> so the SPA can detect a server-side upgrade
/// (or downgrade) and force a full reload.
/// </summary>
public static class HubInfo
{
    /// <summary>
    /// The Hub semver as read from <c>{StoragePath}/ClientApp/version.txt</c>.
    /// <c>null</c> if the marker is missing (legacy installs).
    /// </summary>
    public static string CurrentVersion { get; private set; }

    /// <summary>
    /// Records the Hub version detected at startup. Idempotent; the latest call wins.
    /// </summary>
    public static void Initialize(string version)
    {
        CurrentVersion = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
    }
}
