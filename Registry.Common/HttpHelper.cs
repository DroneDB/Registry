using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Registry.Common;

public static class HttpHelper
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public static async Task DownloadFileAsync(string uri
        , string outputPath)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out _))
            throw new InvalidOperationException("URI is invalid.");

        if (!File.Exists(outputPath))
            throw new FileNotFoundException("File not found."
                , nameof(outputPath));

        var fileBytes = await _httpClient.GetByteArrayAsync(uri);
        await File.WriteAllBytesAsync(outputPath, fileBytes);
    }
}