using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Storage;

/// <summary>
/// One small JSON file in the storage folder, which every node of the deployment shares: written atomically (a reader
/// sees the old content or the new one), read leniently (a missing or damaged file is no document, never an error). A
/// document the disk refuses to take is a <see cref="StorageDocumentException"/>.
/// </summary>
/// <typeparam name="T">The document.</typeparam>
internal sealed class StorageDocument<T> where T : class
{
    private readonly string _path;
    private readonly IFileSystem _fs;
    private readonly AtomicFile _atomic;
    private readonly ILogger _logger;

    public StorageDocument(IOptions<StorageOptions> storage, IFileSystem fs, string fileName, ILogger logger)
    {
        _path = Path.Combine(storage.Value.StoragePath ?? ".", fileName);
        _fs = fs;
        _atomic = new AtomicFile(fs);
        _logger = logger;
    }

    /// <summary>The document, or null when the file is missing or cannot be read.</summary>
    public T? Read()
    {
        string json;
        try
        {
            json = _fs.ReadAllText(_path);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex)
        {
            return Unreadable(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unreadable(ex);
        }

        return Parse(json);
    }

    private T? Unreadable(Exception ex)
    {
        _logger.LogDebug(ex, "Cannot read {Path}: treated as absent", _path);
        return null;
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
    /// <exception cref="StorageDocumentException">The disk refused it.</exception>
    public void Write(T document) => Stored(() =>
    {
        EnsureFolder();
        _atomic.WriteAllText(_path, ApiJson.Serialize(document));
        return true;
    });

    /// <summary>
    /// The document of the file; when there is none yet, <paramref name="document"/> becomes it (see <see cref="AtomicFile.CreateOrRead"/>:
    /// of several nodes that create it together, all get the first one's). Null when the file exists but cannot be read.
    /// </summary>
    /// <exception cref="StorageDocumentException">The disk refused it.</exception>
    public T? CreateOrRead(T document) => Parse(Stored(() =>
    {
        EnsureFolder();
        return _atomic.CreateOrRead(_path, ApiJson.Serialize(document));
    }));

    // The storage folder may not exist yet when the first document is written (the first start of a deployment)
    private void EnsureFolder() => _fs.FolderCreate(Path.GetDirectoryName(Path.GetFullPath(_path))!);

    private TResult Stored<TResult>(Func<TResult> store)
    {
        try
        {
            return store();
        }
        catch (IOException ex)
        {
            throw Refused(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Refused(ex);
        }
    }

    private StorageDocumentException Refused(Exception ex) => new($"Cannot store {_path}: {ex.Message}", ex);
}
