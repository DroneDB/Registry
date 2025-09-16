using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Adapters.DroneDB;
using Registry.Ports;

namespace Registry.Adapters.Thumbnail;

public class LocalThumbnailGenerator : IThumbnailGenerator
{
    private readonly ILogger<LocalThumbnailGenerator> _logger;
    private readonly IDdbWrapper _ddbWrapper;

    public LocalThumbnailGenerator(ILogger<LocalThumbnailGenerator> logger, IDdbWrapper ddbWrapper)
    {
        _logger = logger;
        _ddbWrapper = ddbWrapper;
    }

    public async Task GenerateThumbnailAsync(string filePath, int size, Stream output)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(output);

        if (size < 1)
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be greater than 0.");

        if (!File.Exists(filePath))
        {
            _logger.LogError("File not found: '{FilePath}' - cannot generate thumbnail", filePath);
            throw new FileNotFoundException($"File not found: '{filePath}'", filePath);
        }

        _logger.LogInformation("Starting local thumbnail generation for file: '{FilePath}', size: {Size}", filePath, size);

        try
        {
            var data = _ddbWrapper.GenerateThumbnail(filePath, size);
            await output.WriteAsync(data);

            _logger.LogInformation("Successfully generated local thumbnail for '{FilePath}', size: {Size}, data: {DataSize} bytes",
                filePath, size, data.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate local thumbnail for '{FilePath}', size: {Size}. Error: {ErrorMessage}",
                filePath, size, ex.Message);
            throw new InvalidOperationException($"Failed to generate local thumbnail for '{filePath}': {ex.Message}", ex);
        }
    }
}