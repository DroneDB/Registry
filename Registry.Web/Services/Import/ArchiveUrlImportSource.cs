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
using Registry.Ports.Archives;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;

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
    private readonly ILogger<ArchiveUrlImportSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchiveUrlImportSource"/> class.
    /// </summary>
    /// <param name="extractor">The archive extractor.</param>
    /// <param name="ssrfGuard">The SSRF guard.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public ArchiveUrlImportSource(IArchiveExtractor extractor, SsrfGuard ssrfGuard,
        IHttpClientFactory httpClientFactory, ILogger<ArchiveUrlImportSource> logger)
    {
        _extractor = extractor;
        _ssrfGuard = ssrfGuard;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string SourceType => "archive-url";

    /// <inheritdoc />
    public async Task<ImportSourceProbe> ProbeAsync(JsonElement parameters, CancellationToken ct)
    {
        var p = ReadParams(parameters);
        await _ssrfGuard.AssertAllowedAsync(p.Host, ct);

        var client = _httpClientFactory.CreateClient();
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
            // _extractor.Open throws on a truly unsupported/corrupt archive (surfaced as a task failure).
            ExtractInto(scratch, destFolder, progress, ct);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    private async Task DownloadToFileAsync(ArchiveParams p, string scratch, IProgress<ImportProgress> progress,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, p.Url);
        ApplyAuth(request, p);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var http = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(scratch);

        var buffer = new byte[1024 * 1024];
        long downloaded = 0;
        int read;
        while ((read = await http.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            // Compressed download is reported WITHOUT BytesSoFar so it does not consume the
            // (uncompressed) storage budget; the extraction phase reports the real on-disk bytes.
            progress.Report(new ImportProgress(
                total is > 0 ? Math.Min(0.5, 0.5 * downloaded / total.Value) : -1,
                Phase: "downloading",
                Message: $"Downloaded {downloaded:N0} bytes"));
        }
    }

    private void ExtractInto(string scratch, string destFolder, IProgress<ImportProgress> progress,
        CancellationToken ct)
    {
        using var session = _extractor.Open(scratch);
        var total = session.FastFileEntryCount ?? 0;
        long bytesSoFar = 0;
        var done = 0;

        foreach (var entry in session.Entries())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory) continue;

            var relative = SafeRelative(entry.Key);
            if (relative is null) continue; // skipped (.ddb or unsafe)

            var localTarget = Path.Combine(destFolder, relative.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(localTarget);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            using (var source = entry.OpenStream())
            using (var fileStream = File.Create(localTarget))
                source.CopyTo(fileStream);

            bytesSoFar += SafeLength(localTarget);
            done++;
            progress.Report(new ImportProgress(
                total > 0 ? 0.5 + 0.5 * done / total : -1,
                Phase: "extracting",
                Message: entry.Key,
                BytesSoFar: bytesSoFar,
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
        if (Path.IsPathRooted(key) || key.Split('/').Any(seg => seg == ".."))
            throw new InvalidOperationException($"Unsafe archive entry path (zip-slip): '{entryKey}'.");

        if (key.StartsWith(IDDB.DatabaseFolderName + "/", StringComparison.Ordinal))
            return null;

        return key;
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

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
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
}
