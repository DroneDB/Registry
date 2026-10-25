#nullable enable
using System;
using System.Linq;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// The public URL of the deployment as the update check reports it: the configured external URL, else the one requests
/// were served on (<see cref="ObservedBaseUrlStore"/>), else none.
/// </summary>
public sealed class InstanceBaseUrl
{
    /// <summary>The longest URL that fits the contract.</summary>
    public const int MaxLength = 512;

    private readonly HttpHostOptions _http;
    private readonly ObservedBaseUrlStore _observed;

    public InstanceBaseUrl(IOptions<HttpHostOptions> http, ObservedBaseUrlStore observed)
    {
        _http = http.Value;
        _observed = observed;
    }

    /// <summary>The URL, or null when there is none (or it does not fit the contract: absolute http(s), no spaces, short).</summary>
    public string? Current()
    {
        var url = !string.IsNullOrWhiteSpace(_http.ExternalUrlOverride) ? _http.ExternalUrlOverride.Trim() : _observed.Read();
        return Fits(url) ? url!.TrimEnd('/') : null;
    }

    private static bool Fits(string? url) =>
        url is { Length: > 0 and <= MaxLength } &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        !url.Any(char.IsWhiteSpace);
}
