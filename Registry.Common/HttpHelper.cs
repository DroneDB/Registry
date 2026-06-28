using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.Retry;

namespace Registry.Common;

public static class HttpHelper
{
    // 24 hours timeout for very large downloads (160GB+ datasets)
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromHours(24)
    };

    // 1MB buffer for efficient large file transfers
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Downloads a file from the specified URI with progress reporting support.
    /// </summary>
    /// <param name="uri">The URI to download from</param>
    /// <param name="outputPath">The local path to save the file to</param>
    /// <param name="headers">Optional HTTP headers to include in the request</param>
    /// <param name="progressCallback">Optional callback for progress reporting (bytesDownloaded, totalBytes). totalBytes may be -1 if unknown.</param>
    /// <param name="httpClient">Optional client to use (e.g. an SSRF-hardened one); falls back to the shared static client.</param>
    public static async Task DownloadFileAsync(string uri, string outputPath, Dictionary<string, string> headers = null, Action<long, long> progressCallback = null, HttpClient httpClient = null)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out _))
            throw new InvalidOperationException("URI is invalid.");

        var client = httpClient ?? HttpClient;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        // Add custom headers if provided
        if (headers != null)
        {
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // Use streaming to avoid loading entire file in memory
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;

        await using var sourceStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        long totalBytesRead = 0;
        int bytesRead;
        var lastProgressReport = DateTime.UtcNow;

        while ((bytesRead = await sourceStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalBytesRead += bytesRead;

            // Report progress every 5 seconds or at completion
            if (progressCallback != null && (DateTime.UtcNow - lastProgressReport).TotalSeconds >= 5)
            {
                progressCallback(totalBytesRead, totalBytes);
                lastProgressReport = DateTime.UtcNow;
            }
        }

        // Final progress report
        progressCallback?.Invoke(totalBytesRead, totalBytes);
    }

    /// <summary>
    /// Result of a file download operation with retry support
    /// </summary>
    public class DownloadResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int RetryCount { get; set; }
        public long BytesDownloaded { get; set; }
    }

    /// <summary>
    /// Downloads a file from the specified URI directly to the destination path with retry support using Polly.
    /// Uses exponential backoff for retries. For files larger than the threshold, streams directly to disk.
    /// </summary>
    /// <param name="uri">The URI to download from</param>
    /// <param name="outputPath">The local path to save the file to</param>
    /// <param name="headers">Optional HTTP headers to include in the request</param>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 3)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="httpClient">Optional client to use (e.g. an SSRF-hardened one); falls back to the shared static client.</param>
    /// <returns>Download result with success status and any error details</returns>
    public static async Task<DownloadResult> DownloadFileWithRetryAsync(
        string uri,
        string outputPath,
        Dictionary<string, string> headers = null,
        int maxRetries = 3,
        CancellationToken cancellationToken = default,
        HttpClient httpClient = null)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out _))
            return new DownloadResult { Success = false, ErrorMessage = "URI is invalid." };

        var retryCount = 0;

        // Build retry pipeline with exponential backoff
        var retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(2),
                // Honor the server's Retry-After (429/503) when present; otherwise fall back to the
                // exponential+jitter backoff above. Capped so a hostile header cannot stall forever.
                DelayGenerator = static args =>
                {
                    if (args.Outcome.Exception is RetryableHttpException { RetryAfter: { } ra } && ra > TimeSpan.Zero)
                    {
                        var capped = ra > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : ra;
                        return ValueTask.FromResult<TimeSpan?>(capped);
                    }

                    return ValueTask.FromResult<TimeSpan?>(null);
                },
                OnRetry = args =>
                {
                    retryCount = args.AttemptNumber;
                    // Clean up partial file on retry
                    if (File.Exists(outputPath))
                    {
                        try { File.Delete(outputPath); } catch { /* ignore */ }
                    }
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

        try
        {
            long bytesDownloaded = 0;

            await retryPipeline.ExecuteAsync(async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);

                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                using var response = await (httpClient ?? HttpClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                // Transient throttling/upstream errors are retried with the server's Retry-After delay.
                if (IsRetryableStatus(response.StatusCode))
                    throw new RetryableHttpException(response.StatusCode, ReadRetryAfter(response));

                response.EnsureSuccessStatusCode();

                // Ensure parent directory exists
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                await using var sourceStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: BufferSize, useAsync: true);

                var buffer = new byte[BufferSize];
                int bytesRead;
                bytesDownloaded = 0;

                while ((bytesRead = await sourceStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    bytesDownloaded += bytesRead;
                }
            }, cancellationToken);

            return new DownloadResult
            {
                Success = true,
                RetryCount = retryCount,
                BytesDownloaded = bytesDownloaded
            };
        }
        catch (OperationCanceledException)
        {
            // Clean up partial file on cancellation
            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { /* ignore */ }
            }
            return new DownloadResult
            {
                Success = false,
                ErrorMessage = "Download was cancelled.",
                RetryCount = retryCount
            };
        }
        catch (Exception ex)
        {
            // Clean up partial file on error
            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { /* ignore */ }
            }
            return new DownloadResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                RetryCount = retryCount
            };
        }
    }

    // Status codes worth retrying: rate limiting and transient upstream failures.
    private static bool IsRetryableStatus(System.Net.HttpStatusCode status)
        => status is System.Net.HttpStatusCode.TooManyRequests        // 429
            or System.Net.HttpStatusCode.BadGateway                   // 502
            or System.Net.HttpStatusCode.ServiceUnavailable           // 503
            or System.Net.HttpStatusCode.GatewayTimeout;              // 504

    // Reads the Retry-After header as a delay (supports both delta-seconds and HTTP-date forms).
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null) return null;

        if (retryAfter.Delta.HasValue)
            return retryAfter.Delta;

        if (retryAfter.Date.HasValue)
        {
            var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Signals an HTTP response whose status code is transient and should be retried, optionally
    /// carrying the server-provided <c>Retry-After</c> delay.
    /// </summary>
    private sealed class RetryableHttpException : Exception
    {
        public TimeSpan? RetryAfter { get; }

        public RetryableHttpException(System.Net.HttpStatusCode statusCode, TimeSpan? retryAfter)
            : base($"Server returned {(int)statusCode} ({statusCode}).")
            => RetryAfter = retryAfter;
    }
}
