using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Registry.Ports.Json;
using Registry.Web.Utilities;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Import;

/// <summary>
/// Default <see cref="IRemoteRegistryClient"/>. Self-contained HTTP client mirroring the proven remote
/// transfer logic (authenticate -> list -> parallel download). Downloads stream to disk with retry via
/// <see cref="HttpHelper.DownloadFileWithRetryAsync"/>.
/// </summary>
public sealed class RemoteRegistryClient : IRemoteRegistryClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ImportSettings _settings;
    private readonly DatasetPathPolicy _paths;
    private readonly ILogger<RemoteRegistryClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteRegistryClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="settings">The import settings (download concurrency / retry policy).</param>
    /// <param name="logger">The logger.</param>
    public RemoteRegistryClient(IHttpClientFactory httpClientFactory, ImportSettings settings,
        DatasetPathPolicy paths, ILogger<RemoteRegistryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    // Every outbound call goes through the SSRF-hardened client (connect-time IP validation + redirect
    // guard). The host is also pre-validated by SsrfGuard before each operation in RegistryImportSource.
    private HttpClient CreateClient() => _httpClientFactory.CreateClient(SsrfHttpHandler.HttpClientName);

    /// <inheritdoc />
    public async Task<string?> AuthenticateAsync(string registryUrl, string username, string password,
        CancellationToken ct)
    {
        // Anonymous access: nothing to authenticate.
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(password))
            return null;

        var client = CreateClient();

        var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password)
        ]);

        var response = await client.PostAsync($"{registryUrl.TrimEnd('/')}/users/authenticate", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Remote authentication failed with status {StatusCode}", response.StatusCode);
            return null;
        }

        var result = await response.Content.ReadAsStringAsync(ct);
        using var authResponse = JsonDocument.Parse(result);

        return authResponse.RootElement.Member("token").Text();
    }

    /// <inheritdoc />
    public async Task<EntryDto[]> ListFilesAsync(string registryUrl, string? authToken, string orgSlug,
        string dsSlug, CancellationToken ct)
    {
        var client = CreateClient();
        if (!string.IsNullOrWhiteSpace(authToken))
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {authToken}");

        var searchUrl = $"{DatasetUrl(registryUrl, orgSlug, dsSlug)}/search";

        var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("query", "*"),
            new KeyValuePair<string, string>("recursive", "true")
        ]);

        var response = await client.PostAsync(searchUrl, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to list remote files with status {StatusCode}", response.StatusCode);
            return [];
        }

        var result = await response.Content.ReadAsStringAsync(ct);
        var entries = ApiJson.Deserialize<EntryDto[]>(result) ?? [];

        return [.. entries.Where(e => e.Type != EntryType.Directory)];
    }

    /// <inheritdoc />
    public async Task DownloadFilesParallelAsync(string registryUrl, string? authToken, string sourceOrg,
        string sourceDs, string destFolder, EntryDto[] files, IProgress<ImportProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destFolder);

        // The remote registry is untrusted: every path is validated up front (rooted/".." paths fail
        // the import before anything is downloaded, so a malicious/compromised remote cannot write
        // outside the dataset folder), and reserved paths are skipped: ".ddb" index files are rebuilt
        // locally during indexing/build, and the ".uploads" staging folder is the Registry's own.
        // Extension policy (allow-list / block-list): disallowed file types (e.g. executables/scripts)
        // are skipped here too, mirroring every other ingestion path (ArchiveExtractTool,
        // ArchiveUrlImportSource, PhotogrammetryTool, FileUrlImportTool).
        var toDownload = _paths.ResolveUntrustedPaths(destFolder, files, e => e.Path)
            .Where(t =>
            {
                if (_settings.IsExtensionAllowed(t.Item.Path)) return true;
                _logger.LogWarning("Skipping disallowed file type during registry import: '{Path}'", t.Item.Path);
                return false;
            })
            .ToArray();

        var totalFiles = toDownload.Length;
        var filesDone = 0;
        long bytesSoFar = 0;

        // Conservative, configurable concurrency: remote registries (e.g. hub.dronedb.app) rate-limit
        // aggressively, so a low default avoids tripping HTTP 429. Per-file downloads retry with backoff
        // and honor Retry-After (see HttpHelper.DownloadFileWithRetryAsync).
        var maxParallel = Math.Max(1, _settings.RegistryDownloadConcurrency);

        await Parallel.ForEachAsync(
            toDownload,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
            async (target, token) =>
            {
                var (entry, localPath) = target;

                // Resumability: a file already present at the expected size is treated as complete.
                if (File.Exists(localPath) && new FileInfo(localPath).Length == entry.Size)
                {
                    Interlocked.Add(ref bytesSoFar, entry.Size);
                }
                else
                {
                    var result = await DownloadFileAsync(registryUrl, authToken, sourceOrg, sourceDs, entry.Path,
                        localPath, token);

                    if (!result.Success)
                        throw new IOException(
                            $"Failed to download '{entry.Path}': {result.ErrorMessage ?? "unknown error"}");

                    Interlocked.Add(ref bytesSoFar, result.BytesDownloaded);
                }

                var done = Interlocked.Increment(ref filesDone);
                var currentBytes = Interlocked.Read(ref bytesSoFar);
                progress?.Report(new ImportProgress(
                    totalFiles == 0 ? 1.0 : (double)done / totalFiles,
                    Phase: "downloading",
                    Message: $"Downloaded {done}/{totalFiles} files",
                    BytesSoFar: currentBytes,
                    FilesDone: done,
                    FilesTotal: totalFiles));
            });
    }

    /// <inheritdoc />
    public Task<HttpHelper.DownloadResult> DownloadFileAsync(string registryUrl, string? authToken, string orgSlug,
        string dsSlug, string entryPath, string localPath, CancellationToken ct) =>
        HttpHelper.DownloadFileWithRetryAsync(
            $"{DatasetUrl(registryUrl, orgSlug, dsSlug)}/download?path={Uri.EscapeDataString(entryPath)}&inline=1",
            localPath, BearerHeaders(authToken), Math.Max(1, _settings.RegistryDownloadMaxRetries), CreateClient(), ct);

    /// <inheritdoc />
    public Task DownloadArchiveAsync(string registryUrl, string? authToken, string orgSlug, string dsSlug,
        string destFile, CancellationToken ct) =>
        HttpHelper.DownloadFileAsync($"{DatasetUrl(registryUrl, orgSlug, dsSlug)}/download", destFile,
            BearerHeaders(authToken), (downloaded, total) => _logger.LogDebug(
                "Archive download progress: {Downloaded:N0} / {Total:N0} bytes", downloaded, total),
            CreateClient(), ct);

    private static string DatasetUrl(string registryUrl, string orgSlug, string dsSlug) =>
        $"{registryUrl.TrimEnd('/')}/orgs/{orgSlug}/ds/{dsSlug}";

    private static Dictionary<string, string> BearerHeaders(string? authToken) =>
        string.IsNullOrWhiteSpace(authToken) ? [] : new() { ["Authorization"] = $"Bearer {authToken}" };

    /// <inheritdoc />
    public async Task<RemoteBrowseItem[]> ListOrganizationsAsync(string registryUrl, string? authToken,
        CancellationToken ct)
    {
        var baseUrl = registryUrl.TrimEnd('/');
        var client = CreateClient();
        if (!string.IsNullOrWhiteSpace(authToken))
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {authToken}");

        // Try the authenticated endpoint first; fall back to the public one on 401.
        var response = await client.GetAsync($"{baseUrl}/orgs", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            response = await CreateClient().GetAsync($"{baseUrl}/orgs/public", ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to list remote organizations with status {StatusCode}",
                response.StatusCode);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return BrowseItems(json);
    }

    /// <inheritdoc />
    public async Task<RemoteBrowseItem[]> ListDatasetsAsync(string registryUrl, string? authToken,
        string orgSlug, CancellationToken ct)
    {
        var client = CreateClient();
        if (!string.IsNullOrWhiteSpace(authToken))
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {authToken}");

        var response = await client.GetAsync(
            $"{registryUrl.TrimEnd('/')}/orgs/{Uri.EscapeDataString(orgSlug)}/ds", ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to list remote datasets for org '{OrgSlug}' with status {StatusCode}",
                orgSlug, response.StatusCode);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return BrowseItems(json);
    }

    // The slug and the name of each object of a remote listing (an organization, a dataset)
    private static RemoteBrowseItem[] BrowseItems(string json)
    {
        using var listing = JsonDocument.Parse(json);
        return
        [
            .. listing.RootElement.Items()
                .Select(o => new RemoteBrowseItem(
                    Slug: o.Member("slug").Text() ?? string.Empty,
                    Name: o.Member("name").Text() ?? string.Empty))
                .Where(o => !string.IsNullOrWhiteSpace(o.Slug))
        ];
    }
}
