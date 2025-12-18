using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

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
    public static async Task DownloadFileAsync(string uri, string outputPath, Dictionary<string, string> headers = null, Action<long, long> progressCallback = null)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out _))
            throw new InvalidOperationException("URI is invalid.");

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
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
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
}