#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Registry.Web.Utilities;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>Reads the answer of the update check service. What the service sends is untrusted: it is checked and cut to the contract.</summary>
public static partial class UpdateCheckResponseParser
{
    /// <summary>The most releases kept.</summary>
    public const int MaxReleases = 30;

    /// <summary>The most characters of release notes kept (over all releases).</summary>
    public const int MaxNotesLength = 256 * 1024;

    private const int MaxUrlLength = 2048;
    private const int MaxVersionLength = 64;

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex ReleaseVersion();

    /// <summary>Parses the body of an answer.</summary>
    /// <exception cref="UpdateCheckException">The body is not the answer of the contract: not JSON, not an object, or a member of the wrong type.</exception>
    public static UpdateCheckResponse Parse(ReadOnlySpan<byte> body)
    {
        UpdateCheckResponse? raw;
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("latestVersion", out _))
                throw new UpdateCheckException("The answer is not an update check answer");

            raw = root.Deserialize<UpdateCheckResponse>(ApiJson.Options);
        }
        catch (JsonException ex)
        {
            throw new UpdateCheckException("The answer is not valid JSON of the update check contract", ex);
        }

        if (raw is null)
            throw new UpdateCheckException("The answer is empty");

        var (releases, truncated) = Releases(raw.Releases ?? [], raw.ReleasesTruncated);
        return new UpdateCheckResponse(
            Text(raw.LatestVersion, MaxVersionLength),
            Date(raw.ReleaseDate),
            WebUrl(raw.ChangelogUrl, https: false),
            WebUrl(raw.CompareUrl, https: false),
            releases,
            truncated,
            WebUrl(raw.LicenseRequestUrl, https: true));
    }

    private static (ReleaseInfo[] Releases, bool Truncated) Releases(ReleaseInfo?[] received, bool truncated)
    {
        var kept = new List<ReleaseInfo>();
        var notesLength = 0;

        foreach (var release in received.OfType<ReleaseInfo>().Where(r => r.Version is not null && ReleaseVersion().IsMatch(r.Version)))
        {
            var notes = release.Notes ?? string.Empty;
            if (kept.Count == MaxReleases || notesLength + notes.Length > MaxNotesLength)
            {
                // Only the newest releases fit: the range is cut, and the notes say so
                truncated = true;
                break;
            }

            notesLength += notes.Length;
            kept.Add(new ReleaseInfo(release.Version, Date(release.Date) ?? string.Empty, WebUrl(release.Url, https: false) ?? string.Empty, notes));
        }

        return ([.. kept], truncated);
    }

    private static string? Text(string? value, int maxLength) =>
        UpdateCheckPayloadBuilder.Fit(value, maxLength);

    private static string? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    /// <summary><paramref name="value"/> when it is an absolute http(s) URL (https only with <paramref name="https"/>) without spaces; otherwise null.</summary>
    private static string? WebUrl(string? value, bool https) =>
        value is { Length: > 0 and <= MaxUrlLength } &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || !https && uri.Scheme == Uri.UriSchemeHttp) &&
        !value.Any(char.IsWhiteSpace)
            ? value
            : null;
}
