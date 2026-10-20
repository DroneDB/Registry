using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;

namespace Registry.Adapters.Thumbnail;

/// <summary>
/// HTTP-based thumbnail generator that calls a remote service.
/// </summary>
public class RemoteThumbnailGenerator : IThumbnailGenerator
{
    private readonly ILogger<RemoteThumbnailGenerator> _logger;
    private readonly RemoteThumbnailGeneratorSettings _settings;
    private static readonly HttpClient SharedHttpClient;

    static RemoteThumbnailGenerator()
    {
        SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public RemoteThumbnailGenerator(ILogger<RemoteThumbnailGenerator> logger,
        IOptions<RemoteThumbnailGeneratorSettings> options)
    {
        _logger = logger;
        _settings = options.Value;
    }

    public async Task GenerateThumbnailAsync(string filePath, int size, Stream output)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(output);

        if (size < 1)
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be greater than 0.");

        if (!File.Exists(filePath))
        {
            _logger.LogError("File not found: '{FilePath}' - cannot generate remote thumbnail", filePath);
            throw new FileNotFoundException($"File not found: '{filePath}'", filePath);
        }

        _logger.LogInformation("Starting remote thumbnail generation for file: '{FilePath}', size: {Size}, target URL: {Url}",
            filePath, size, _settings.Url);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = CreateRequest(_settings, filePath, size);

            _logger.LogDebug("Sending POST request to remote thumbnail generator: {Url}", _settings.Url);
            using var response = await SharedHttpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                stopwatch.Stop();
                _logger.LogError("Remote thumbnail generation failed for '{FilePath}', size: {Size}. HTTP Status: {StatusCode}, Reason: {ReasonPhrase}, elapsed: {ElapsedMs}ms",
                    filePath, size, response.StatusCode, response.ReasonPhrase, stopwatch.ElapsedMilliseconds);
                throw new InvalidOperationException($"Remote thumbnail generation failed with status {response.StatusCode}: {response.ReasonPhrase}");
            }

            await response.Content.CopyToAsync(output);

            stopwatch.Stop();
            var contentLength = response.Content.Headers.ContentLength ?? output.Length;
            _logger.LogInformation("Successfully generated remote thumbnail for '{FilePath}', size: {Size}, data: ~{DataSize} bytes, elapsed: {ElapsedMs}ms",
                filePath, size, contentLength, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed to generate remote thumbnail for '{FilePath}', size: {Size} after {ElapsedMs}ms. Error: {ErrorMessage}",
                filePath, size, stopwatch.ElapsedMilliseconds, ex.Message);
            throw new InvalidOperationException($"Failed to generate remote thumbnail for '{filePath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds the <c>POST</c> request sent to the ThumbnailGenerator host for <paramref name="filePath"/>,
    /// authenticated with <see cref="RemoteThumbnailGeneratorSettings.Token"/>.
    /// </summary>
    public static HttpRequestMessage CreateRequest(RemoteThumbnailGeneratorSettings settings, string filePath,
        int size)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var request = new HttpRequestMessage(HttpMethod.Post, settings.Url)
        {
            Content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("path", filePath),
                new KeyValuePair<string, string>("size", size.ToString(CultureInfo.InvariantCulture))
            ])
        };

        // The host answers 401 without the shared token
        if (!string.IsNullOrEmpty(settings.Token))
            request.Headers.Add(RemoteThumbnailGeneratorSettings.TokenHeaderName, settings.Token);

        return request;
    }
}
