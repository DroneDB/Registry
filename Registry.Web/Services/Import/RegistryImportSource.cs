#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.Import;

namespace Registry.Web.Services.Import;

/// <summary>
/// Import source that pulls a dataset from another DroneDB Registry / Hub instance
/// (<see cref="SourceType"/> = <c>registry</c>). Authentication is optional (anonymous for public
/// datasets). The actual transfer is delegated to <see cref="IRemoteRegistryClient"/>.
/// </summary>
public sealed class RegistryImportSource : IImportSource
{
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

        var bytes = files.Sum(f => f.Size);
        return new ImportSourceProbe(true, null, bytes, files.Length, p.Dataset);
    }

    /// <inheritdoc />
    public async Task FetchAsync(JsonElement parameters, string destFolder, IProgress<ImportProgress> progress,
        CancellationToken ct)
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
            progress, ct);
    }

    private static RegistryParams ReadParams(JsonElement parameters)
    {
        var url = GetString(parameters, "url")
                  ?? throw new ArgumentException("A registry URL is required.");
        var org = GetString(parameters, "organization")
                  ?? throw new ArgumentException("A source organization is required.");
        var dataset = GetString(parameters, "dataset")
                      ?? throw new ArgumentException("A source dataset is required.");
        var username = GetString(parameters, "username") ?? string.Empty;
        var password = GetString(parameters, "password") ?? string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"The registry URL is not a valid http/https URL: {url}");

        return new RegistryParams(url, uri.Host, org, dataset, username, password);
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
