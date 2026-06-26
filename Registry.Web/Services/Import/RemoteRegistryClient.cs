#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Utilities;

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
    private readonly ILogger<RemoteRegistryClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteRegistryClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="settings">The import settings (download concurrency / retry policy).</param>
    /// <param name="logger">The logger.</param>
    public RemoteRegistryClient(IHttpClientFactory httpClientFactory, ImportSettings settings,
        ILogger<RemoteRegistryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> AuthenticateAsync(string registryUrl, string username, string password,
        CancellationToken ct)
    {
        // Anonymous access: nothing to authenticate.
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(password))
            return null;

        var client = _httpClientFactory.CreateClient();

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
        var authResponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(result);

        return authResponse?.SafeGetValue("token") as string;
    }

    /// <inheritdoc />
    public async Task<EntryDto[]> ListFilesAsync(string registryUrl, string? authToken, string orgSlug,
        string dsSlug, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        if (!string.IsNullOrWhiteSpace(authToken))
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {authToken}");

        var searchUrl = $"{registryUrl.TrimEnd('/')}/orgs/{orgSlug}/ds/{dsSlug}/search";

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
        var entries = JsonConvert.DeserializeObject<EntryDto[]>(result) ?? [];

        return entries.Where(e => e.Type != EntryType.Directory).ToArray();
    }

    /// <inheritdoc />
    public async Task DownloadFilesParallelAsync(string registryUrl, string? authToken, string sourceOrg,
        string sourceDs, string destFolder, EntryDto[] files, IProgress<ImportProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destFolder);

        // Exclude internal index files - those are rebuilt locally during indexing/build.
        var toDownload = files
            .Where(e => !e.Path.StartsWith(".ddb/", StringComparison.Ordinal)
                        && !e.Path.StartsWith(".ddb\\", StringComparison.Ordinal))
            .ToArray();

        var totalFiles = toDownload.Length;
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(authToken))
            headers.Add("Authorization", $"Bearer {authToken}");

        var filesDone = 0;
        long bytesSoFar = 0;

        // Conservative, configurable concurrency: remote registries (e.g. hub.dronedb.app) rate-limit
        // aggressively, so a low default avoids tripping HTTP 429. Per-file downloads retry with backoff
        // and honor Retry-After (see HttpHelper.DownloadFileWithRetryAsync).
        var maxParallel = Math.Max(1, _settings.RegistryDownloadConcurrency);
        var maxRetries = Math.Max(1, _settings.RegistryDownloadMaxRetries);

        await Parallel.ForEachAsync(
            toDownload,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
            async (entry, token) =>
            {
                var localPath = Path.Combine(destFolder, entry.Path.Replace('/', Path.DirectorySeparatorChar));

                // Resumability: a file already present at the expected size is treated as complete.
                if (File.Exists(localPath) && new FileInfo(localPath).Length == entry.Size)
                {
                    Interlocked.Add(ref bytesSoFar, entry.Size);
                }
                else
                {
                    var downloadUrl =
                        $"{registryUrl.TrimEnd('/')}/orgs/{sourceOrg}/ds/{sourceDs}/download" +
                        $"?path={Uri.EscapeDataString(entry.Path)}&inline=1";

                    var result = await HttpHelper.DownloadFileWithRetryAsync(
                        downloadUrl, localPath, headers, maxRetries, token);

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
}
