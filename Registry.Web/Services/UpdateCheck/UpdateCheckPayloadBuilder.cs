using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// Builds the body of the update check request from the instance identity, the version of the build, the machine, the
/// configuration and what the administrator declared. Every value is made to fit the contract (truncated, stripped, or
/// left null), so the body is always valid.
/// </summary>
public sealed partial class UpdateCheckPayloadBuilder
{
    /// <summary>The longest operating system description and runtime description.</summary>
    public const int MaxDescriptionLength = 32;

    /// <summary>The longest host name.</summary>
    public const int MaxHostNameLength = 64;

    /// <summary>The longest domain name.</summary>
    public const int MaxDomainLength = 253;

    private readonly IInstanceIdentity _identity;
    private readonly UpdateCheckSettingsStore _settings;
    private readonly InstanceBaseUrl _baseUrl;
    private readonly IPlatformFacts _platform;
    private readonly DataStoreOptions _data;
    private readonly HostResourceBudget _host;
    private readonly IDdbWrapper _ddb;

    public UpdateCheckPayloadBuilder(IInstanceIdentity identity, UpdateCheckSettingsStore settings, InstanceBaseUrl baseUrl,
        IPlatformFacts platform, IOptions<DataStoreOptions> data, HostResourceBudget host, IDdbWrapper ddb)
    {
        _identity = identity;
        _settings = settings;
        _baseUrl = baseUrl;
        _platform = platform;
        _data = data.Value;
        _host = host;
        _ddb = ddb;
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex RegistrySemver();

    [GeneratedRegex(@"^\d+\.\d+(\.\d+)?$")]
    private static partial Regex LibddbSemver();

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex NotDomainCharacter();

    /// <summary>The payload for this node, now.</summary>
    /// <exception cref="InvalidOperationException">This build has no version of three numeric parts (the request cannot be valid without it).</exception>
    public UpdateCheckPayload Build()
    {
        var identity = _identity.Current;
        var settings = _settings.Read();
        var platform = _platform.Capture();

        var version = RegistryVersion.Current;
        if (version is null || !RegistrySemver().IsMatch(version))
            throw new InvalidOperationException($"The Registry version '{version}' is not major.minor.patch");

        return new UpdateCheckPayload
        {
            InstanceId = identity.Id,
            InstalledAt = identity.InstalledAt,
            Version = version,
            DdbVersion = DdbVersion(),
            Os = platform.Os,
            OsCaption = Fit(platform.OsDescription, MaxDescriptionLength),
            Arch = platform.Architecture is "x64" or "arm64" or "x86" or "arm" ? platform.Architecture : null,
            Runtime = Fit(platform.FrameworkDescription, MaxDescriptionLength) ?? string.Empty,
            Env = platform.InContainer ? "docker" : "native",
            InstanceType = JsonNamingPolicy.CamelCase.ConvertName(_host.Role.ToString()),
            Db = _data.RegistryProvider.ToString().ToLowerInvariant(),
            BaseUrl = _baseUrl.Current(),
            Hostname = Fit(platform.HostName, MaxHostNameLength),
            Domain = Domain(platform.DomainName),
            LicenseRef = null,
            UseDeclaration = settings.UseDeclaration,
            // Only with the consent the administrator gave: without it the field is not sent at all
            ContactEmail = settings.ConsentedEmail
        };
    }

    // The libddb this build is pinned to; a build that does not carry the pin asks the library it runs
    private string? DdbVersion()
    {
        var version = LibddbVersion.Pinned ?? LibddbVersion.Semver(_ddb.GetVersion());
        return version is not null && LibddbSemver().IsMatch(version) ? version : null;
    }

    private static string? Domain(string? domain) =>
        Fit(domain is null ? null : NotDomainCharacter().Replace(domain, string.Empty), MaxDomainLength);

    /// <summary>
    /// <paramref name="value"/> without control characters, trimmed and cut to <paramref name="maxLength"/> characters (never
    /// inside a surrogate pair); null for no text.
    /// </summary>
    public static string? Fit(string? value, int maxLength)
    {
        if (value is null)
            return null;

        var fitted = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (fitted.Length > maxLength)
        {
            fitted = fitted[..maxLength];
            if (char.IsHighSurrogate(fitted[^1]))
                fitted = fitted[..^1];
            fitted = fitted.TrimEnd();
        }

        return fitted.Length == 0 ? null : fitted;
    }
}
