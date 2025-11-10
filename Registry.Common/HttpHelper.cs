using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Registry.Common;

public static class HttpHelper
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public static Task DownloadFileAsync(string uri, string outputPath)
    {
        return DownloadFileAsync(uri, outputPath, null);
    }

    public static async Task DownloadFileAsync(string uri, string outputPath, Dictionary<string, string> headers)
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
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var sourceStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 8192, useAsync: true);

        await sourceStream.CopyToAsync(fileStream);
    }
}