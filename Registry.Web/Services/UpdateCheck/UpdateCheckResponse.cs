#nullable enable
namespace Registry.Web.Services.UpdateCheck;

/// <summary>One published release in the answer of the update check.</summary>
/// <param name="Version">The version, three numeric parts, without a leading "v".</param>
/// <param name="Date">The publication date (<c>YYYY-MM-DD</c>).</param>
/// <param name="Url">The release page.</param>
/// <param name="Notes">The release notes, in Markdown.</param>
public sealed record ReleaseInfo(string Version, string Date, string Url, string Notes);

/// <summary>
/// The answer of the update check service (<c>Contracts/update-check-response.v1.schema.json</c> is the contract): the
/// latest version and the releases between the installed one and it.
/// </summary>
/// <param name="LatestVersion">The latest published version; null when there is none.</param>
/// <param name="ReleaseDate">When it was published (<c>YYYY-MM-DD</c>).</param>
/// <param name="ChangelogUrl">The page of the latest release.</param>
/// <param name="CompareUrl">The page that compares the installed version with the latest.</param>
/// <param name="Releases">The releases after the installed version up to the latest, newest first.</param>
/// <param name="ReleasesTruncated">The service left out the oldest releases of the range.</param>
/// <param name="LicenseRequestUrl">The form to request a licence: used only when it is an absolute HTTPS URL.</param>
public sealed record UpdateCheckResponse(
    string? LatestVersion,
    string? ReleaseDate,
    string? ChangelogUrl,
    string? CompareUrl,
    ReleaseInfo[] Releases,
    bool ReleasesTruncated,
    string? LicenseRequestUrl);
