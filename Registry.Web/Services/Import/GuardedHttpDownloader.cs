#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Services.Import;

/// <summary>
/// Result of a single-file URL probe (the "verify" step): reachability plus best-effort size and
/// server-suggested file name.
/// </summary>
/// <param name="Reachable">True when the URL responded successfully.</param>
/// <param name="Message">Human-readable status/error message, or null on success.</param>
/// <param name="SizeBytes">The advertised Content-Length, when known.</param>
/// <param name="SuggestedFileName">The Content-Disposition file name, when provided by the server.</param>
public sealed record UrlProbeResult(
    bool Reachable, string? Message, long? SizeBytes, string? SuggestedFileName);

/// <summary>Incremental progress emitted while downloading a single file.</summary>
/// <param name="Fraction">Completion fraction in 0..1, or -1 when the total size is unknown.</param>
/// <param name="BytesSoFar">Bytes written so far.</param>
/// <param name="TotalBytes">Total bytes when known, otherwise null.</param>
public sealed record FileDownloadProgress(double Fraction, long BytesSoFar, long? TotalBytes);

/// <summary>
/// SSRF-hardened single-file HTTP downloader shared by the verify path and the <c>import-file</c>
/// heavy tool. Every request is pre-validated by <see cref="SsrfGuard"/> and issued through the
/// SSRF-hardened <c>import-ssrf</c> client (connect-time IP validation + redirect guard). The
/// download stream is bounded by a hard size cap and disk head-room (via <see cref="ExtractionBudget"/>)
/// and a low-speed guard that aborts stalled or too-slow transfers.
/// </summary>
public sealed class GuardedHttpDownloader
{
    private const int CopyBufferSize = 1024 * 1024;

    private readonly SsrfGuard _ssrfGuard;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GuardedHttpDownloader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GuardedHttpDownloader"/> class.
    /// </summary>
    /// <param name="ssrfGuard">The SSRF guard used to pre-validate the target host.</param>
    /// <param name="httpClientFactory">Factory for the SSRF-hardened named client.</param>
    /// <param name="logger">The logger.</param>
    public GuardedHttpDownloader(SsrfGuard ssrfGuard, IHttpClientFactory httpClientFactory,
        ILogger<GuardedHttpDownloader> logger)
    {
        _ssrfGuard = ssrfGuard;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // Outbound calls go through the SSRF-hardened client (connect-time IP validation + redirect guard).
    private HttpClient CreateClient() => _httpClientFactory.CreateClient(SsrfHttpHandler.HttpClientName);

    /// <summary>
    /// Probes <paramref name="url"/> without transferring the body: reports reachability, the
    /// advertised size and the server-suggested file name. Tries HEAD first and falls back to a
    /// headers-only GET for servers that do not support HEAD.
    /// </summary>
    /// <param name="url">The absolute http/https URL to probe.</param>
    /// <param name="username">Optional HTTP basic-auth user name.</param>
    /// <param name="password">Optional HTTP basic-auth password.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The probe result.</returns>
    public async Task<UrlProbeResult> ProbeAsync(Uri url, string? username, string? password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        await _ssrfGuard.AssertAllowedAsync(url.Host, ct);

        var client = CreateClient();
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            ApplyAuth(head, username, password);
            using var headResp = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            if (headResp.IsSuccessStatusCode)
                return Reachable(headResp);

            // Some servers reject HEAD (405/501). Fall back to a headers-only GET.
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(get, username, password);
            using var getResp = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
            return getResp.IsSuccessStatusCode
                ? Reachable(getResp)
                : new UrlProbeResult(false, $"The URL is not reachable (HTTP {(int)getResp.StatusCode}).", null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UrlProbeResult(false, $"The URL could not be reached: {ex.Message}", null, null);
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destFile"/>, enforcing the size cap, the
    /// disk head-room and the low-speed guard. Throws on a breach so the caller can roll back.
    /// </summary>
    /// <param name="url">The absolute http/https URL to download.</param>
    /// <param name="destFile">The destination file path (its folder is created if missing).</param>
    /// <param name="username">Optional HTTP basic-auth user name.</param>
    /// <param name="password">Optional HTTP basic-auth password.</param>
    /// <param name="maxBytes">Absolute size cap in bytes; <c>0</c> disables the cap.</param>
    /// <param name="minSpeedBytesPerSec">Minimum average speed in bytes/second; <c>0</c> disables the low-speed guard.</param>
    /// <param name="lowSpeedGraceSeconds">Window (seconds) over which the average speed is measured.</param>
    /// <param name="diskSafetyMarginBytes">Disk head-room to keep free; <c>0</c> disables the disk guard.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of bytes downloaded.</returns>
    /// <exception cref="QuotaExceededException">The size cap or disk-space margin would be breached.</exception>
    /// <exception cref="TimeoutException">The transfer stalled or stayed below the minimum speed.</exception>
    public async Task<long> DownloadAsync(Uri url, string destFile, string? username, string? password,
        long maxBytes, long minSpeedBytesPerSec, int lowSpeedGraceSeconds, long diskSafetyMarginBytes,
        IProgress<FileDownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        await _ssrfGuard.AssertAllowedAsync(url.Host, ct);

        var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request, username, password);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;

        // Fail fast when the advertised size already exceeds the cap (avoids starting the transfer).
        if (maxBytes > 0 && total is > 0 && total.Value > maxBytes)
            throw new QuotaExceededException(
                $"The file is too large ({CommonUtils.GetBytesReadable(total.Value)}); " +
                $"the maximum allowed size is {CommonUtils.GetBytesReadable(maxBytes)}.");

        var destFolder = Path.GetDirectoryName(destFile);
        if (string.IsNullOrEmpty(destFolder)) destFolder = Path.GetTempPath();
        Directory.CreateDirectory(destFolder);

        // Size cap (primary) + disk head-room (secondary) enforced per chunk, before each write.
        var budget = new ExtractionBudget(maxBytes, destFolder, diskSafetyMarginBytes);

        await using var http = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destFile);

        var buffer = new byte[CopyBufferSize];
        long downloaded = 0;

        var lowSpeedOn = minSpeedBytesPerSec > 0 && lowSpeedGraceSeconds > 0;
        var window = Stopwatch.StartNew();
        long windowStartBytes = 0;

        while (true)
        {
            int read;
            if (lowSpeedOn)
            {
                // Hard stall guard: a single read that outlasts the grace window means ~0 B/s.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(TimeSpan.FromSeconds(lowSpeedGraceSeconds + 5));
                try
                {
                    read = await http.ReadAsync(buffer, readCts.Token);
                }
                catch (OperationCanceledException) when (readCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException("The download stalled (no data received).");
                }
            }
            else
            {
                read = await http.ReadAsync(buffer, ct);
            }

            if (read <= 0) break;

            // Enforce the size cap + disk head-room BEFORE writing the chunk.
            budget.Account(read);
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            // Rolling low-speed guard: at each grace boundary the window's average speed must meet
            // the minimum, otherwise the transfer is aborted.
            if (lowSpeedOn && window.Elapsed.TotalSeconds >= lowSpeedGraceSeconds)
            {
                var windowBytes = downloaded - windowStartBytes;
                var speed = windowBytes / window.Elapsed.TotalSeconds;
                if (speed < minSpeedBytesPerSec)
                    throw new TimeoutException(
                        $"The download is too slow ({speed:F0} B/s is below the required " +
                        $"{minSpeedBytesPerSec} B/s over {lowSpeedGraceSeconds}s).");

                window.Restart();
                windowStartBytes = downloaded;
            }

            progress?.Report(new FileDownloadProgress(
                total is > 0 ? Math.Min(1.0, (double)downloaded / total.Value) : -1, downloaded, total));
        }

        return downloaded;
    }

    private static UrlProbeResult Reachable(HttpResponseMessage response)
    {
        var size = response.Content.Headers.ContentLength;
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = disposition?.FileNameStar ?? disposition?.FileName;
        return new UrlProbeResult(true, null, size is > 0 ? size : null, TrimQuotes(fileName));
    }

    private static void ApplyAuth(HttpRequestMessage request, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var raw = Encoding.UTF8.GetBytes($"{username}:{password}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
    }

    private static string? TrimQuotes(string? s) => s?.Trim().Trim('"');
}
