using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;

namespace Registry.Web.Models.DTO;

public enum FileDescriptorType
{
    Single, Multiple, Dataset
}

/// <summary>
/// A download: one file, or a ZIP archive of a selection or of the whole dataset, written straight
/// to the response (nothing is buffered, the first bytes leave while later files are still
/// being read).
/// </summary>
public class FileStreamDescriptor
{
    private readonly string[] _paths;
    private readonly string[] _folders;
    private readonly FileDescriptorType _descriptorType;
    private readonly ILogger _logger;
    private readonly IDDB _ddb;
    private readonly IZipArchiveBuilder _zipBuilder;

    public string Name { get; }

    public string ContentType { get; }

    /// <summary>
    /// Classification of the underlying download (single file, multi-file selection, or full dataset).
    /// Used by callers (e.g. <see cref="Services.Objects.IEntryQueryService.DownloadAsync"/>) to enforce
    /// bulk-download policies.
    /// </summary>
    public FileDescriptorType Type => _descriptorType;

    public FileStreamDescriptor(string name, string contentType, IDDB ddb, string[] paths, string[] folders,
        FileDescriptorType descriptorType, ILogger logger, IZipArchiveBuilder zipBuilder)
    {
        _paths = paths;
        _folders = folders;
        _descriptorType = descriptorType;
        _logger = logger;
        _zipBuilder = zipBuilder;
        _ddb = ddb;
        Name = name;
        ContentType = contentType;
    }

    /// <summary>Writes the download to <paramref name="stream"/>; stops when <paramref name="ct"/> is cancelled.</summary>
    public async Task CopyToAsync(Stream stream, CancellationToken ct = default)
    {
        if (_descriptorType == FileDescriptorType.Single)
        {
            var filePath = _paths.First();
            _logger.LogInformation("Only one path found: '{FilePath}'", filePath);

            await using var fileStream = new FileStream(_ddb.GetLocalPath(filePath), FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await fileStream.CopyToAsync(stream, ct);
            return;
        }

        _logger.LogInformation("Streaming a ZIP archive of {PathCount} files", _paths?.Length ?? 0);
        await _zipBuilder.WriteZipAsync(_ddb, _paths ?? [], _folders ?? [],
            _descriptorType == FileDescriptorType.Dataset, stream, progress: null, ct);
    }
}
