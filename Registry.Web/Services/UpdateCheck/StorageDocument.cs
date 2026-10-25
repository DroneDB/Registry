#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Services.UpdateCheck;

/// <summary>
/// One small JSON file in the storage folder, which every node of the deployment shares: written atomically (a reader
/// sees the old content or the new one), read leniently (a missing or damaged file is no document, never an error).
/// </summary>
/// <typeparam name="T">The document.</typeparam>
internal sealed class StorageDocument<T> where T : class
{
    private readonly string _path;
    private readonly ILogger _logger;

    public StorageDocument(IOptions<StorageOptions> storage, string fileName, ILogger logger)
    {
        _path = Path.Combine(storage.Value.StoragePath ?? ".", fileName);
        _logger = logger;
    }

    /// <summary>The document, or null when the file is missing or cannot be read.</summary>
    public T? Read()
    {
        try
        {
            return Parse(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Cannot read {Path}: treated as absent", _path);
            return null;
        }
    }

    private T? Parse(string json)
    {
        try
        {
            return ApiJson.Deserialize<T>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "{Path} is not valid: treated as absent", _path);
            return null;
        }
    }

    /// <summary>Replaces the file with <paramref name="document"/>.</summary>
    public void Write(T document)
    {
        EnsureFolder();
        AtomicFile.WriteAllText(_path, ApiJson.Serialize(document));
    }

    /// <summary>
    /// The document of the file; when there is none yet, <paramref name="document"/> becomes it (see <see cref="AtomicFile.CreateOrRead"/>:
    /// of several nodes that create it together, all get the first one's). Null when the file exists but cannot be read.
    /// </summary>
    public T? CreateOrRead(T document)
    {
        EnsureFolder();
        return Parse(AtomicFile.CreateOrRead(_path, ApiJson.Serialize(document)));
    }

    // The storage folder may not exist yet when the first document is written (the first start of a deployment)
    private void EnsureFolder() => Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
}
