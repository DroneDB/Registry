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
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Ports;

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
    private readonly ScratchFileProvider _scratch;
    private readonly ILogger<RemoteRegistryClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteRegistryClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="settings">The import settings (download concurrency / retry policy).</param>
    /// <param name="paths">The dataset path policy (the untrusted paths of a remote listing).</param>
    /// <param name="scratch">Where a file is downloaded before it is moved into the dataset.</param>
    /// <param name="logger">The logger.</param>
    public RemoteRegistryClient(IHttpClientFactory httpClientFactory, ImportSettings settings,
        DatasetPathPolicy paths, ScratchFileProvider scratch, ILogger<RemoteRegistryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _paths = paths;
        _scratch = scratch;
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

        var result = await ReadJsonAsync(response, registryUrl, ct);
        var entries = ApiJson.Deserialize<EntryDto[]>(result) ?? [];

        return [.. entries.Where(e => e.Type != EntryType.Directory)];
    }

    /// <inheritdoc />
    public async Task DownloadFilesParallelAsync(string registryUrl, string? authToken, string sourceOrg,
        string sourceDs, string destFolder, EntryDto[] files, IImportWriteRecord writes,
        IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writes);
        Directory.CreateDirectory(destFolder);

        // The remote registry is untrusted: every path is validated up front (rooted/".." paths fail
        // the import before anything is downloaded, so a malicious/compromised remote cannot write
        // outside the dataset folder), and reserved paths are skipped: ".ddb" index files are rebuilt
        // locally during indexing/build, and the ".uploads" staging folder is the Registry's own.
        // Extension policy (allow-list / block-list): disallowed file types (e.g. executables/scripts)
        // are skipped here too, mirroring every other ingestion path (ArchiveExtractTool,
        // ArchiveUrlImportSource, PhotogrammetryTool, FileUrlImportTool).
        var allowed = _paths.ResolveUntrustedPaths(destFolder, files, e => e.Path)
            .Where(t =>
            {
                if (_settings.IsExtensionAllowed(t.Item.Path)) return true;
                _logger.LogWarning("Skipping disallowed file type during registry import: '{Path}'", t.Item.Path);
                return false;
            })
            .ToArray();

        // A path listed twice (or in two letter cases, on a file system that ignores them) is one file: the last entry
        // wins, as in an archive extraction, and it is downloaded and moved once
        var last = new Dictionary<string, int>(PathComparison.Comparer);
        for (var i = 0; i < allowed.Length; i++)
            last[allowed[i].LocalPath] = i;
        var toDownload = allowed.Where((t, i) => last[t.LocalPath] == i).ToArray();

        var totalFiles = toDownload.Length;
        var filesDone = 0;
        long bytesSoFar = 0;
        // The listed sizes of the files done: the progress never passes the listed total, whatever the remote sends
        long listedBytesDone = 0;

        // The progress follows the bytes (FR-UPL-21): the files of a dataset differ in size by orders of magnitude, so
        // a fraction of files done made the estimate of the time left several times too long
        var totalBytes = toDownload.Sum(t => Math.Max(0, t.Item.Size));

        // Conservative, configurable concurrency: remote registries (e.g. hub.dronedb.app) rate-limit
        // aggressively, so a low default avoids tripping HTTP 429. Per-file downloads retry with backoff
        // and honor Retry-After (see HttpHelper.DownloadFileWithRetryAsync).
        var maxParallel = Math.Max(1, _settings.RegistryDownloadConcurrency);

        // The files this run moved into the dataset (local paths): never forgotten from the record
        var moved = new HashSet<string>(StringComparer.Ordinal);

        // A batch of files at a time (FR-XC-27, §8.1): downloaded in parallel to temporary files of the uploads folder,
        // which is never part of the dataset; then their destinations and folders are recorded and saved with one change
        // of the write record, and only then moved into place. A process lost at any point leaves no file in the dataset
        // that the record does not hold. A cancel drops the files of the batch not moved yet
        foreach (var batch in toDownload.Chunk(Math.Max(1, PlaceBatchSize)))
        {
            var fetched = new FetchedFile?[batch.Length];
            try
            {
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, batch.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
                    async (i, token) =>
                    {
                        var (entry, localPath) = batch[i];
                        var path = Path.GetRelativePath(destFolder, localPath).Replace('\\', '/');

                        fetched[i] = await FetchFileAsync(registryUrl, authToken, sourceOrg, sourceDs, destFolder, entry,
                            path, localPath, writes, token);

                        var done = Interlocked.Increment(ref filesDone);
                        var currentBytes = Interlocked.Add(ref bytesSoFar, fetched[i]?.Bytes ?? 0);
                        var listedDone = Interlocked.Add(ref listedBytesDone, Math.Max(0, entry.Size));
                        progress?.Report(new ImportProgress(
                            ProgressFraction(done, totalFiles, listedDone, totalBytes),
                            Phase: "downloading",
                            Message: $"Downloaded {done}/{totalFiles} files",
                            BytesSoFar: currentBytes,
                            FilesDone: done,
                            FilesTotal: totalFiles));
                    });

                await PlaceAsync(destFolder, fetched, writes, moved, ct);
            }
            finally
            {
                foreach (var file in fetched)
                    file?.Temp?.Dispose();
            }
        }
    }

    /// <summary>
    /// The completion fraction of a transfer (FR-UPL-21): the listed sizes of the files done of the listed sizes of the
    /// files to download (a file listed at 0 bytes is done when it ends, and weighs nothing), or, when the sizes are not
    /// known (every listed size is 0), the files done of the files to download. It is 1 only when the last file ended:
    /// files still to come keep it at <see cref="LastFilesFraction"/> at most.
    /// </summary>
    internal static double ProgressFraction(int filesDone, int totalFiles, long listedBytesDone, long totalBytes)
    {
        if (totalFiles <= 0 || filesDone >= totalFiles)
            return 1.0;

        var fraction = totalBytes > 0 ? (double)listedBytesDone / totalBytes : (double)filesDone / totalFiles;
        return Math.Min(Math.Clamp(fraction, 0.0, 1.0), LastFilesFraction);
    }

    /// <summary>The most a transfer is done while a file is still to come (a whole-number percentage shows 99).</summary>
    internal const double LastFilesFraction = 0.99;

    /// <summary>How many files are downloaded, recorded and moved into the dataset together.</summary>
    internal int PlaceBatchSize { get; init; } = TaskImportWrites.SaveBatch;

    // A file of the import, downloaded (Temp: its temporary file) or complete already (Temp null: written by an earlier
    // attempt of the task)
    private sealed record FetchedFile(string Path, string LocalPath, ScratchFile? Temp, long Bytes);

    // One file of the import (FR-XC-27): downloaded to a temporary file in the uploads folder of the dataset, which is
    // never part of it. A file that is not the task's is kept and skipped (null).
    private async Task<FetchedFile?> FetchFileAsync(string registryUrl, string? authToken, string sourceOrg,
        string sourceDs, string destFolder, EntryDto entry, string path, string localPath, IImportWriteRecord writes,
        CancellationToken ct)
    {
        if (File.Exists(localPath))
        {
            if (!writes.MayWrite(path))
            {
                _logger.LogWarning("Skipping '{Path}' of the registry import: the file is already in the dataset", path);
                return null;
            }

            // Resumability: a file this task wrote before, already at the expected size, is complete
            if (new FileInfo(localPath).Length == entry.Size)
                return new FetchedFile(path, localPath, null, entry.Size);

            // The task's own, incomplete or different: written again
            File.Delete(localPath);
        }

        var temp = _scratch.CreateForDataset(destFolder);
        try
        {
            var result = await DownloadFileAsync(registryUrl, authToken, sourceOrg, sourceDs, entry.Path, temp.Path, ct);

            // HttpHelper.DownloadFileWithRetryAsync reports a cancellation as a failure: a cancel (the user's, the quota
            // stop of D-039) is a cancellation, not an I/O error of this file
            ct.ThrowIfCancellationRequested();

            if (!result.Success)
                throw new IOException($"Failed to download '{entry.Path}': {result.ErrorMessage ?? "unknown error"}");

            return new FetchedFile(path, localPath, temp, result.BytesDownloaded);
        }
        catch
        {
            temp.Dispose();
            throw;
        }
    }

    // The downloaded files of a batch go into the dataset: their destinations and new folders are recorded, each file
    // stamped from its complete temporary file (a move keeps size and time), the record is saved, and only then the
    // folders are created and the files moved, never over a file. A file that appeared meanwhile is kept, and is not the
    // task's
    private async Task PlaceAsync(string destFolder, IReadOnlyList<FetchedFile?> fetched, IImportWriteRecord writes,
        HashSet<string> moved, CancellationToken ct)
    {
        var toMove = new List<FetchedFile>(fetched.Count);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in fetched)
        {
            if (file is null)
                continue;

            if (file.Temp is null)
            {
                // Complete already, the task's own: recorded with its stamp
                writes.Record(file.Path);
                writes.Written(file.Path);
                continue;
            }

            if (!writes.MayWrite(file.Path))
            {
                // Somebody created it while this one was downloaded: kept, not the task's
                _logger.LogWarning("Skipping '{Path}' of the registry import: the file appeared meanwhile", file.Path);
                continue;
            }

            TaskImportWrites.RecordFolders(writes, destFolder, TaskImportWrites.ParentOf(file.Path), folders);
            writes.Record(file.Path);
            writes.Written(file.Path, file.Temp.Path);
            toMove.Add(file);
        }

        if (toMove.Count == 0)
        {
            await writes.FlushIfDueAsync(ct);
            return;
        }

        await TaskImportWrites.SaveAsync(writes, ct);

        foreach (var file in toMove)
        {
            TaskImportWrites.CreateFolder(destFolder, TaskImportWrites.ParentOf(file.Path));
            try
            {
                File.Move(file.Temp!.Path, file.LocalPath, overwrite: false);
                file.Temp.Release();
                moved.Add(file.LocalPath);
            }
            catch (IOException) when (File.Exists(file.LocalPath))
            {
                // Created between the check and the move: never replaced. Not the task's, unless this run moved it
                // there (the same file under another letter case): a file the task wrote stays in its record
                _logger.LogWarning("Skipping '{Path}' of the registry import: the file appeared meanwhile", file.Path);
                if (!MovedByThisRun(file.LocalPath, moved))
                    await writes.ForgetAsync(file.Path, CancellationToken.None);
            }
        }
    }

    // Whether the file at localPath is one this run moved. On a file system that ignores letter case the path can name
    // a file moved under another letter case: its exact name is then not in its folder
    private static bool MovedByThisRun(string localPath, HashSet<string> moved)
    {
        if (moved.Contains(localPath))
            return true;

        var folder = Path.GetDirectoryName(localPath);
        var name = Path.GetFileName(localPath);
        if (string.IsNullOrEmpty(folder) || Directory.EnumerateFileSystemEntries(folder)
                .Any(e => string.Equals(Path.GetFileName(e), name, StringComparison.Ordinal)))
            return false;

        return moved.Any(m => string.Equals(m, localPath, StringComparison.OrdinalIgnoreCase));
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
    public async Task<RemoteDatasetInfo?> GetDatasetInfoAsync(string registryUrl, string? authToken, string orgSlug,
        string dsSlug, CancellationToken ct)
    {
        try
        {
            var client = CreateClient();
            if (!string.IsNullOrWhiteSpace(authToken))
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {authToken}");

            var response = await client.GetAsync(DatasetUrl(registryUrl, orgSlug, dsSlug), ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("The remote dataset info of '{OrgSlug}/{DsSlug}' answered {StatusCode}", orgSlug,
                    dsSlug, response.StatusCode);
                return null;
            }

            using var dataset = JsonDocument.Parse(await ReadJsonAsync(response, registryUrl, ct));
            return MetaOf(dataset.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The name is a convenience: the import goes on with the slug
            _logger.LogInformation(ex, "Could not read the remote dataset info of '{OrgSlug}/{DsSlug}'", orgSlug, dsSlug);
            return null;
        }
    }

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

        var json = await ReadJsonAsync(response, registryUrl, ct);
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

        var json = await ReadJsonAsync(response, registryUrl, ct);
        return BrowseItems(json);
    }

    /// <summary>The message of a registry URL that answers with something other than the JSON of the API.</summary>
    internal static string NotARegistryMessage(string registryUrl) =>
        $"{registryUrl} does not answer as a DroneDB Registry. Enter the address of the registry " +
        "(for example https://hub.dronedb.app) or the link of a dataset (https://hub.dronedb.app/r/odm/brighton-beach).";

    // The body of a successful answer of the API. The web app of a registry (or any other site) answers
    // the API paths below a wrong address with an HTML page and 200: a 400 that names the address,
    // not a JSON parse failure surfacing as a 500.
    private static async Task<string> ReadJsonAsync(HttpResponseMessage response, string registryUrl,
        CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException(NotARegistryMessage(registryUrl), noRetry: true);

        return await response.Content.ReadAsStringAsync(ct);
    }

    // The display name and the tagline of a dataset, from properties.meta.<key>.data; null when it has neither
    private static RemoteDatasetInfo? MetaOf(JsonElement dataset)
    {
        var name = MetaData(dataset, "name");
        var tagline = MetaData(dataset, "tagline");
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        tagline = string.IsNullOrWhiteSpace(tagline) ? null : tagline.Trim();
        return name is null && tagline is null ? null : new RemoteDatasetInfo(name, tagline);
    }

    // properties.meta.<key>.data of a dataset, as text
    private static string? MetaData(JsonElement dataset, string key) =>
        dataset.Member("properties") is { } properties && properties.Member("meta") is { } meta &&
        meta.Member(key) is { } entry
            ? entry.Member("data").Text()
            : null;

    // The slug and the name of each object of a remote listing (an organization, a dataset)
    private static RemoteBrowseItem[] BrowseItems(string json)
    {
        using var listing = JsonDocument.Parse(json);
        return
        [
            .. listing.RootElement.Items()
                .Select(o => new RemoteBrowseItem(
                    Slug: o.Member("slug").Text() ?? string.Empty,
                    // An organization has a name; a dataset keeps its display name in its meta (FR-UPL-14)
                    Name: o.Member("name").Text() ?? MetaOf(o)?.Name ?? string.Empty))
                .Where(o => !string.IsNullOrWhiteSpace(o.Slug))
        ];
    }
}
