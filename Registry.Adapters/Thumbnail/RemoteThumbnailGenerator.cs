using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;

namespace Registry.Adapters.Thumbnail;

public class RemoteThumbnailGenerator : IThumbnailGenerator
{
    private readonly ILogger<RemoteThumbnailGenerator> _logger;
    private readonly RemoteThumbnailGeneratorSettings _settings;

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
            throw new FileNotFoundException("File not found.", filePath);

        _logger.LogInformation("Generating remote thumbnail for {File} with size {Size}", filePath, size);

        try
        {
            using var client = new HttpClient();
            var content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("path", filePath),
                new KeyValuePair<string, string>("size", size.ToString())
            ]);

            // Keep the timeout short to avoid blocking the thread for too long
            client.Timeout = TimeSpan.FromSeconds(30);

            var response = await client.PostAsync(_settings.Url, content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to generate remote thumbnail for {FilePath} with size {Size}", filePath, size);
                throw new InvalidOperationException("Failed to generate remote thumbnail.");
            }

            await response.Content.CopyToAsync(output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate remote thumbnail for {FilePath} with size {Size}", filePath, size);
            throw new InvalidOperationException("Failed to generate remote thumbnail.", ex);
        }
    }

}