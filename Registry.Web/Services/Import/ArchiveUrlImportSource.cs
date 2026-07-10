#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.Archives;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Services.Import;

/// <summary>
/// Import source that downloads a compressed archive over HTTP(S) and extracts it into the dataset
/// (<see cref="SourceType"/> = <c>archive-url</c>). The archive is streamed to a scratch file OUTSIDE
/// the dataset folder, extracted with zip-slip protection, then the scratch file is removed.
/// </summary>
public sealed class ArchiveUrlImportSource : IImportSource
{
    private static readonly string[] ArchiveExtensions =
        [".zip", ".tar.gz", ".tgz", ".tar", ".7z", ".rar"];

    private readonly IArchiveExtractor _extractor;
    private readonly SsrfGuard _ssrfGuard;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GuardedHttpDownloader _downloader;
    private readonly ImportSettings _settings;
    private readonly long _maxImportSizeBytes;
    private readonly long _diskSafetyMarginBytes;
    private readonly ILogger<ArchiveUrlImportSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchiveUrlImportSource"/> class.
    /// </summary>
    /// <param name="extractor">The archive extractor.</param>
    /// <param name="ssrfGuard">The SSRF guard.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="downloader">The SSRF-hardened, disk- and speed-guarded single-file downloader.</param>
    /// <param name="appSettings">The application settings (import cap + disk safety margin).</param>
    /// <param name="logger">The logger.</param>
    public ArchiveUrlImportSource(IArchiveExtractor extractor, SsrfGuard ssrfGuard,
        IHttpClientFactory httpClientFactory, GuardedHttpDownloader downloader,
        IOptions<AppSettings> appSettings, ILogger<ArchiveUrlImportSource> logger)
    {
        _extractor = extractor;
        _ssrfGuard = ssrfGuard;
        _httpClientFactory = httpClientFactory;
        _downloader = downloader;
        _settings = appSettings.Value.Import ?? new ImportSettings();
        _maxImportSizeBytes = _settings.MaxImportSizeBytes;
        _diskSafetyMarginBytes =
            (appSettings.Value.ProcessingPlatform ?? new ProcessingPlatformSettings()).DiskSafetyMarginBytes;
        _logger = logger;
    }

    // Outbound calls go through the SSRF-hardened client (connect-time IP validation + redirect
    // guard); the host is also pre-validated by SsrfGuard before probe/fetch.
    private HttpClient CreateClient() => _httpClientFactory.CreateClient(SsrfHttpHandler.HttpClientName);

    /// <inheritdoc />
    public string SourceType => "archive-url";

    /// <inheritdoc />
    public async Task<ImportSourceProbe> ProbeAsync(JsonElement parameters, CancellationToken ct)
    {
        var p = ReadParams(parameters);
        await _ssrfGuard.AssertAllowedAsync(p.Host, ct);

        var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, p.Url);
        ApplyAuth(request, p);

        long? size = null;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new ImportSourceProbe(false, $"The URL is not reachable (HTTP {(int)response.StatusCode}).",
                    null, null, p.SuggestedName);
            size = response.Content.Headers.ContentLength;
        }
        catch (Exception ex)
        {
            return new ImportSourceProbe(false, $"The URL could not be reached: {ex.Message}", null, null,
                p.SuggestedName);
        }

        // EstimatedBytes is the COMPRESSED size (Content-Length); the uncompressed footprint is unknown
        // until extraction, so it is only a lower bound for the storage check.
        return new ImportSourceProbe(true,
            "Estimated size is the compressed archive size; the extracted dataset may be larger.",
            size, null, p.SuggestedName);
    }

    /// <inheritdoc />
    public async Task FetchAsync(JsonElement parameters, string destFolder, IProgress<ImportProgress> progress,
        CancellationToken ct)
    {
        var p = ReadParams(parameters);
        await _ssrfGuard.AssertAllowedAsync(p.Host, ct);

        Directory.CreateDirectory(destFolder);

        // Scratch file lives OUTSIDE the dataset folder so it is never indexed. Keep the original
        // archive extension so the extractor can pick the right format.
        var scratch = Path.Combine(Path.GetTempPath(), $"ddb-import-{Guid.NewGuid():N}{ArchiveExt(p.Url)}");

        try
        {
            await DownloadToFileAsync(p, scratch, progress, ct);

            // Content-based guard: reject a non-archive served with an archive name (e.g. an HTML error
            // page) BEFORE attempting extraction, instead of relying on Open() failing mid-stream.
            if (!_extractor.IsValidArchive(scratch))
                throw new InvalidOperationException(
                    "The downloaded file is not a valid or supported archive.");

            // _extractor.Open throws on a truly unsupported/corrupt archive (surfaced as a task failure).
            await ExtractIntoAsync(scratch, destFolder, progress, ct);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    private async Task DownloadToFileAsync(ArchiveParams p, string scratch, IProgress<ImportProgress> progress,
        CancellationToken ct)
    {
        // Map the downloader's fraction/bytes into the 0..0.5 slice reserved for the download phase.
        // The COMPRESSED size is intentionally NOT capped (maxBytes: 0): what counts against the
        // storage budget is the EXTRACTED footprint, enforced per-entry in ExtractIntoAsync. We still
        // apply the low-speed/stall guard and the temp-volume disk head-room here (via the downloader).
        var sink = new CallbackProgress<FileDownloadProgress>(fp =>
            progress.Report(new ImportProgress(
                fp.TotalBytes is > 0 ? Math.Min(0.5, 0.5 * fp.Fraction) : -1,
                Phase: "downloading",
                Message: fp.TotalBytes is > 0
                    ? $"Downloaded {CommonUtils.GetBytesReadable(fp.BytesSoFar)} / {CommonUtils.GetBytesReadable(fp.TotalBytes.Value)}"
                    : $"Downloaded {CommonUtils.GetBytesReadable(fp.BytesSoFar)}")));

        await _downloader.DownloadAsync(
            url: new Uri(p.Url),
            destFile: scratch,
            username: string.IsNullOrWhiteSpace(p.Username) ? null : p.Username,
            password: string.IsNullOrWhiteSpace(p.Password) ? null : p.Password,
            maxBytes: 0,
            minSpeedBytesPerSec: _settings.MinDownloadSpeedBytesPerSec,
            lowSpeedGraceSeconds: _settings.LowSpeedGraceSeconds,
            diskSafetyMarginBytes: _diskSafetyMarginBytes,
            progress: sink,
            ct: ct);
    }

    private async Task ExtractIntoAsync(string scratch, string destFolder, IProgress<ImportProgress> progress,
        CancellationToken ct)
    {
        using var session = _extractor.Open(scratch);
        var total = session.FastFileEntryCount ?? 0;

        // Cap (primary, MaxImportSizeBytes) + disk head-room (secondary, re-sampled) consolidated in
        // ExtractionBudget: enforced per chunk, so a single entry whose header under-reports its size
        // cannot fill the volume. The outer ImportDatasetTool sink still enforces the per-user quota.
        var budget = new ExtractionBudget(_maxImportSizeBytes, destFolder, _diskSafetyMarginBytes);
        var done = 0;

        foreach (var entry in session.Entries())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory) continue;

            var relative = SafeRelative(entry.Key);
            if (relative is null) continue; // skipped (.ddb or unsafe)

            // Extension policy (allow-list / block-list): skip disallowed types (e.g. executables
            // or scripts) with a warning; the rest of the archive still extracts.
            if (!_settings.IsExtensionAllowed(relative))
            {
                _logger.LogWarning("Skipping disallowed file type during archive import: '{Path}'", relative);
                continue;
            }

            var localTarget = Path.Combine(destFolder, relative.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(localTarget);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            await using (var source = entry.OpenStream())
            await using (var fileStream = File.Create(localTarget))
                await budget.CopyGuardedAsync(source, fileStream, ct);

            done++;
            progress.Report(new ImportProgress(
                total > 0 ? 0.5 + 0.5 * done / total : -1,
                Phase: "extracting",
                Message: entry.Key,
                BytesSoFar: budget.BytesWritten,
                FilesDone: done,
                FilesTotal: total > 0 ? total : null));
        }
    }

    private static void ApplyAuth(HttpRequestMessage request, ArchiveParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Username)) return;
        var raw = Encoding.UTF8.GetBytes($"{p.Username}:{p.Password}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
    }

    // Anti zip-slip: rejects rooted paths and ".." traversal; skips the reserved .ddb folder.
    private static string? SafeRelative(string entryKey)
    {
        var key = (entryKey ?? string.Empty).Replace('\\', '/').Trim();
        while (key.StartsWith('/')) key = key[1..];

        if (string.IsNullOrWhiteSpace(key)) return null;

        // Reject embedded null bytes: on some native paths a NUL truncates the string, which could
        // sidestep the rooted-path / ".." checks below (defense in depth).
        if (key.Contains('\0'))
            throw new InvalidOperationException($"Unsafe archive entry path (null byte): '{entryKey}'.");

        if (Path.IsPathRooted(key) || key.Split('/').Any(seg => seg == ".."))
            throw new InvalidOperationException($"Unsafe archive entry path (zip-slip): '{entryKey}'.");

        if (key.StartsWith(IDDB.DatabaseFolderName + "/", StringComparison.Ordinal))
            return null;

        // Normalize every path segment (invalid chars, Windows reserved device names, trailing
        // dots/spaces, length); leading dots are preserved (hidden files).
        key = FileImportPolicy.SanitizePathSegments(key);
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private ArchiveParams ReadParams(JsonElement parameters)
    {
        var url = GetString(parameters, "url")
                  ?? throw new ArgumentException("An archive URL is required.");
        var username = GetString(parameters, "username") ?? string.Empty;
        var password = GetString(parameters, "password") ?? string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"The archive URL is not a valid http/https URL: {url}");

        return new ArchiveParams(url, uri.Host, SuggestName(uri), username, password);
    }

    private static string GuessArchiveName(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : url;

    // Returns the recognized multi-part archive extension (e.g. ".tar.gz") or the plain extension.
    private static string ArchiveExt(string url)
    {
        var name = GuessArchiveName(url);
        foreach (var ext in ArchiveExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return ext;
        }

        var plain = Path.GetExtension(name);
        return string.IsNullOrEmpty(plain) ? ".zip" : plain;
    }

    private static string SuggestName(Uri uri)
    {
        var fileName = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName)) return "imported-dataset";

        foreach (var ext in ArchiveExtensions)
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return fileName[..^ext.Length];
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete import scratch file '{Path}'", path);
        }
    }

    private readonly record struct ArchiveParams(
        string Url, string Host, string SuggestedName, string Username, string Password);

    // Synchronous progress adapter (mirrors the one used by the import heavy tools) so callbacks run
    // inline instead of being posted asynchronously by System.Progress<T>.
    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
