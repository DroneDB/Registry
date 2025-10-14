using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Models.DTO;

public enum FileDescriptorType
{
    Single, Multiple, Dataset
}

public class FileStreamDescriptor
{
    private readonly string _orgSlug;
    private readonly Guid _internalRef;
    private readonly string[] _paths;
    private readonly string[] _folders;
    private readonly FileDescriptorType _descriptorType;
    private readonly ILogger<ObjectsManager> _logger;
    private readonly IDdbManager _ddbManager;
    private readonly IDDB _ddb;
    private readonly long _maxZipMemoryThreshold;

    public string Name { get; }

    public string ContentType { get; }

    public FileStreamDescriptor(string name, string contentType, string orgSlug, Guid internalRef, string[] paths, string[] folders,
        FileDescriptorType descriptorType, ILogger<ObjectsManager> logger, IDdbManager ddbManager, long maxZipMemoryThreshold)
    {
        _orgSlug = orgSlug;
        _internalRef = internalRef;
        _paths = paths;
        _folders = folders;
        _descriptorType = descriptorType;
        _logger = logger;
        _ddbManager = ddbManager;
        _maxZipMemoryThreshold = maxZipMemoryThreshold;
        Name = name;
        ContentType = contentType;

        _ddb = ddbManager.Get(orgSlug, internalRef);
    }

    private long CalculateTotalSize()
    {
        if (_paths == null) return 0;

        long totalSize = 0;
        foreach (var path in _paths)
        {
            try
            {
                var localPath = _ddb.GetLocalPath(path);
                var fileInfo = new FileInfo(localPath);
                
                // NOTE: This could be optimized by caching file sizes during initial listing or using Length without checking existence (catch exception if not found)
                if (fileInfo.Exists)
                    totalSize += fileInfo.Length;
                
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not get file size for path: '{Path}'", path);
            }
        }

        _logger.LogDebug("Total size for {PathCount} files: {TotalSize} bytes", _paths.Length, totalSize);
        return totalSize;
    }

    private async Task CreateZipInMemory(Stream outputStream)
    {
        // Use MemoryStream to avoid synchronous operations with ZipArchive directly on HTTP response stream
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            await AddFilesToZip(archive);
        } // ZipArchive is disposed here safely on MemoryStream

        // Copy the completed ZIP data to the response stream asynchronously
        memoryStream.Seek(0, SeekOrigin.Begin);
        await memoryStream.CopyToAsync(outputStream);
    }

    private async Task CreateZipWithTempFile(Stream outputStream)
    {
        var tempFilePath = Path.GetTempFileName();
        try
        {
            // Create ZIP in temporary file
            await using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                await AddFilesToZip(archive);
            } 
            // ZipArchive is disposed here safely on file stream

            // Stream the temporary file to the response stream
            await using var tempFileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read);
            await tempFileStream.CopyToAsync(outputStream);
        }
        finally
        {
            // Clean up temporary file
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temporary file: {TempFilePath}", tempFilePath);
            }
        }
    }

    private async Task AddFilesToZip(ZipArchive archive)
    {
        foreach (var path in _paths)
        {
            _logger.LogInformation("Zipping: '{Path}'", path);

            var entry = archive.CreateEntry(path, CommonUtils.GetCompressionLevel(path));
            await using var entryStream = entry.Open();

            var localPath = _ddb.GetLocalPath(path);

            await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
            await fileStream.CopyToAsync(entryStream);
        }

        // We treat folders separately because if they are empty they would not be included in the archive
        if (_folders != null)
        {
            foreach (var folder in _folders)
                archive.CreateEntry(folder + "/");
        }

        // Include ddb folder
        if (_descriptorType == FileDescriptorType.Dataset)
        {
            var ddb = _ddbManager.Get(_orgSlug, _internalRef);

            archive.CreateEntryFromAny(
                Path.Combine(ddb.DatasetFolderPath, IDDB.DatabaseFolderName),
                string.Empty, [ddb.BuildFolderPath]);
        }
    }

    public async Task CopyToAsync(Stream stream)
    {
        // If there is just one file we return it
        if (_descriptorType == FileDescriptorType.Single)
        {
            var filePath = _paths.First();

            _logger.LogInformation("Only one path found: '{FilePath}'", filePath);

            var localPath = _ddb.GetLocalPath(filePath);

            await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
            await fileStream.CopyToAsync(stream);

        }
        // Otherwise we zip everything together and return the package
        else
        {
            var totalSize = CalculateTotalSize();
            var useMemoryStream = totalSize <= _maxZipMemoryThreshold;

            _logger.LogInformation("Creating ZIP archive for {PathCount} files, total size: {TotalSize} bytes, using {Method}",
                _paths?.Length ?? 0, totalSize, useMemoryStream ? "memory" : "temporary file");

            if (useMemoryStream)
            {
                await CreateZipInMemory(stream);
            }
            else
            {
                await CreateZipWithTempFile(stream);
            }
        }

    }

}