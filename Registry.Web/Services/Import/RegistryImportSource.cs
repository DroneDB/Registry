using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.Import;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.Import;

/// <summary>
/// Import source that pulls a dataset from another DroneDB Registry / Hub instance
/// (<see cref="SourceType"/> = <c>registry</c>). Authentication is optional (anonymous for public
/// datasets). The actual transfer is delegated to <see cref="IRemoteRegistryClient"/>.
/// </summary>
public sealed class RegistryImportSource : IImportSource, IBrowsableImportSource
{
    /// <summary>
    /// Client-facing message for a remote authentication failure surfaced by the browse endpoints: a
    /// <c>ForbiddenException</c> that never retries, so the signed-in caller gets 403 (never 401, which
    /// would sign them out of this Registry). <c>FetchAsync</c> keeps its own message as
    /// the heavy-task failure reason.
    /// </summary>
    public const string AuthFailureMessage = "Authentication to the remote registry failed. Check the username and password.";

    private readonly IRemoteRegistryClient _client;
    private readonly SsrfGuard _ssrfGuard;

    /// <summary>
    /// Initializes a new instance of the <see cref="RegistryImportSource"/> class.
    /// </summary>
    /// <param name="client">The remote registry transfer client.</param>
    /// <param name="ssrfGuard">The SSRF guard enforcing the outbound-host allow policy.</param>
    public RegistryImportSource(IRemoteRegistryClient client, SsrfGuard ssrfGuard)
    {
        _client = client;
        _ssrfGuard = ssrfGuard;
    }

    /// <inheritdoc />
    public string SourceType => "registry";

    /// <inheritdoc />
    public async Task<ImportSourceProbe> ProbeAsync(JsonElement parameters, CancellationToken ct)
    {
        var p = ReadParams(parameters);
        await _ssrfGuard.AssertAllowedAsync(p.Host, ct);

        var token = await _client.AuthenticateAsync(p.Url, p.Username, p.Password, ct);

        // Credentials were supplied but the remote rejected them.
        if (!string.IsNullOrWhiteSpace(p.Username) && token is null)
            return new ImportSourceProbe(false, "Authentication failed.", null, null, p.Dataset);

        var files = await _client.ListFilesAsync(p.Url, token, p.Organization, p.Dataset, ct);

        if (files.Length == 0)
            return new ImportSourceProbe(false, "The remote dataset was not found or is empty.", 0, 0, p.Dataset);

        // The remote display name and tagline (FR-UPL-14): the dataset keeps what its owner gave it, not the slug
        var info = await _client.GetDatasetInfoAsync(p.Url, token, p.Organization, p.Dataset, ct);

        var bytes = files.Sum(f => f.Size);
        return new ImportSourceProbe(true, null, bytes, files.Length, info?.Name ?? p.Dataset, info?.Tagline, p.Dataset);
    }

    /// <inheritdoc />
    public async Task FetchAsync(JsonElement parameters, string destFolder, IImportWriteRecord writes,
        IProgress<ImportProgress> progress, CancellationToken ct)
    {
        var p = ReadParams(parameters);
        await _ssrfGuard.AssertAllowedAsync(p.Host, ct);

        var token = await _client.AuthenticateAsync(p.Url, p.Username, p.Password, ct);

        // Credentials were supplied but the remote rejected them.
        if (!string.IsNullOrWhiteSpace(p.Username) && token is null)
            throw new InvalidOperationException("Authentication failed.");

        var files = await _client.ListFilesAsync(p.Url, token, p.Organization, p.Dataset, ct);

        if (files.Length == 0)
            throw new InvalidOperationException("The remote dataset was not found or is empty.");

        await _client.DownloadFilesParallelAsync(p.Url, token, p.Organization, p.Dataset, destFolder, files,
            writes, progress, ct);
    }

    private static RegistryParams ReadParams(JsonElement parameters)
    {
        var (url, host, username, password) = ReadConnectionParams(parameters);
        var link = SplitRegistryUrl(new Uri(GetString(parameters, "url")!));
        var org = GetString(parameters, "organization") ?? link.Organization
                  ?? throw new ArgumentException("A source organization is required.");
        var dataset = GetString(parameters, "dataset") ?? link.Dataset
                      ?? throw new ArgumentException("A source dataset is required.");

        return new RegistryParams(url, host, org, dataset, username, password);
    }

    /// <inheritdoc />
    public async Task<RemoteBrowseItem[]> BrowseOrganizationsAsync(JsonElement parameters, CancellationToken ct)
    {
        var (url, host, username, password) = ReadConnectionParams(parameters);
        await _ssrfGuard.AssertAllowedAsync(host, ct);

        var token = await _client.AuthenticateAsync(url, username, password, ct);
        if (!string.IsNullOrWhiteSpace(username) && token is null)
            throw new ForbiddenException(AuthFailureMessage);

        return await _client.ListOrganizationsAsync(url, token, ct);
    }

    /// <inheritdoc />
    public async Task<RemoteBrowseItem[]> BrowseDatasetsAsync(JsonElement parameters, CancellationToken ct)
    {
        var (url, host, username, password) = ReadConnectionParams(parameters);
        var org = GetString(parameters, "organization")
                  ?? SplitRegistryUrl(new Uri(GetString(parameters, "url")!)).Organization
                  ?? throw new ArgumentException("A source organization is required.");
        await _ssrfGuard.AssertAllowedAsync(host, ct);

        var token = await _client.AuthenticateAsync(url, username, password, ct);
        if (!string.IsNullOrWhiteSpace(username) && token is null)
            throw new ForbiddenException(AuthFailureMessage);

        return await _client.ListDatasetsAsync(url, token, org, ct);
    }

    private static (string Url, string Host, string Username, string Password) ReadConnectionParams(
        JsonElement parameters)
    {
        var url = GetString(parameters, "url")
                  ?? throw new ArgumentException("A registry URL is required.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"The registry URL is not a valid http/https URL: {url}");

        var username = GetString(parameters, "username") ?? string.Empty;
        var password = GetString(parameters, "password") ?? string.Empty;
        return (SplitRegistryUrl(uri).BaseUrl, uri.Host, username, password);
    }

    /// <summary>
    /// Splits the URL of a registry into its base address and the organization and the dataset it names
    /// when it is the link of a page (<c>/r/{org}[/{ds}[/...]]</c>) or of the API
    /// (<c>/orgs/{org}[/ds/{ds}[/...]]</c>) rather than the address of the registry: the API paths are
    /// appended to the base address, and appended to the link of a page they reach the web app, which
    /// answers every path with its HTML page. A path before <c>/r/</c> or <c>/orgs/</c> is kept
    /// (a registry served below a prefix); the query and the fragment are dropped.
    /// </summary>
    /// <param name="uri">The absolute http/https URL given for the registry.</param>
    /// <returns>The base address, and the organization and the dataset of the link (null when absent).</returns>
    internal static (string BaseUrl, string? Organization, string? Dataset) SplitRegistryUrl(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        var origin = uri.GetLeftPart(UriPartial.Authority);

        string Base(int count) => count == 0
            ? origin
            : $"{origin}/{string.Join('/', segments.Take(count).Select(Uri.EscapeDataString))}";

        var r = Array.IndexOf(segments, "r");
        if (r >= 0 && r + 1 < segments.Length)
            return (Base(r), segments[r + 1], r + 2 < segments.Length ? segments[r + 2] : null);

        var orgs = Array.IndexOf(segments, "orgs");
        if (orgs >= 0 && orgs + 1 < segments.Length)
            return (Base(orgs), segments[orgs + 1],
                orgs + 3 < segments.Length && segments[orgs + 2] == "ds" ? segments[orgs + 3] : null);

        return (uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), null, null);
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private readonly record struct RegistryParams(
        string Url, string Host, string Organization, string Dataset, string Username, string Password);
}
