#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Import;

namespace Registry.Web.Services.RemoteImport;

/// <summary>
/// <see cref="ImportMode.Archive"/>: the dataset's ZIP (its index included) is downloaded to the temp folder, then
/// replaces the content of the destination and the whole dataset is rebuilt.
/// </summary>
public sealed class ArchiveImportStrategy : IRemoteImportStrategy
{
    private const int ProgressLogSeconds = 10;

    private readonly IRemoteRegistryClient _remote;
    private readonly ImportDestinations _destinations;
    private readonly IFileSystem _fileSystem;
    private readonly StorageOptions _storage;
    private readonly ILogger<ArchiveImportStrategy> _logger;

    public ArchiveImportStrategy(IRemoteRegistryClient remote, ImportDestinations destinations,
        IFileSystem fileSystem, IOptions<StorageOptions> storage, ILogger<ArchiveImportStrategy> logger)
    {
        _remote = remote;
        _destinations = destinations;
        _fileSystem = fileSystem;
        _storage = storage.Value;
        _logger = logger;
    }

    public ImportMode Mode => ImportMode.Archive;

    public async Task ImportAsync(RemoteDataset dataset, ImportReport report, CancellationToken ct)
    {
        var zipPath = Path.Combine(_storage.TempPath!, $"import-{CommonUtils.RandomString(16)}.zip");
        try
        {
            _logger.LogInformation("Downloading {Org}/{Ds} as an archive to {ZipPath}",
                dataset.SourceOrganization, dataset.SourceDataset, zipPath);
            try
            {
                await _remote.DownloadArchiveAsync(dataset.RegistryUrl, dataset.AuthToken, dataset.SourceOrganization,
                    dataset.SourceDataset, zipPath, ct);
            }
            catch (HttpRequestException ex)
            {
                report.Error(dataset, $"Download failed: {ex.Message}", ImportPhase.Download);
                return;
            }

            if (!IsZip(zipPath))
            {
                _logger.LogWarning("The download of {Org}/{Ds} is not a ZIP: {Content}", dataset.SourceOrganization,
                    dataset.SourceDataset, Head(zipPath));
                report.Error(dataset, "Download failed: the server did not return a valid ZIP file. The dataset " +
                                      "may be private or require authentication.", ImportPhase.Download);
                return;
            }

            await ReplaceContentAsync(dataset, zipPath, report, ct);
        }
        finally
        {
            if (_fileSystem.Exists(zipPath))
                _fileSystem.Delete(zipPath);
        }
    }

    private async Task ReplaceContentAsync(RemoteDataset dataset, string zipPath, ImportReport report,
        CancellationToken ct)
    {
        try
        {
            var destination = await _destinations.GetOrCreateAsync(dataset, ct);

            // The archive holds the source index: it replaces everything, the local index included
            _destinations.Empty(destination);
            Extract(zipPath, destination.Folder, ct);

            var files = _fileSystem.GetFiles(destination.Folder, "*", SearchOption.AllDirectories);
            var size = files.Sum(f => _fileSystem.GetFileSize(f));
            _logger.LogInformation("Extracted {FileCount} files ({Size:N0} bytes) into {Folder}", files.Length, size,
                destination.Folder);

            await _destinations.QueueBuildAsync(destination, _destinations.Open(destination), ct);
            report.Imported(destination, size, files.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Cannot import the archive of {Org}/{Ds}", dataset.SourceOrganization,
                dataset.SourceDataset);
            report.Error(dataset, ex.Message, ImportPhase.Save);
        }
    }

    // Extracts every entry under destPath; an entry that would land outside it is skipped
    private void Extract(string zipPath, string destPath, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(destPath) + Path.DirectorySeparatorChar;
        var total = archive.Entries.Count;
        var done = 0;
        var lastLog = DateTime.UtcNow;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destPath, entry.FullName));

            if (!target.StartsWith(root, StringComparison.Ordinal))
                _logger.LogWarning("Skipping the ZIP entry {EntryName}: it is outside the dataset", entry.FullName);
            else if (string.IsNullOrEmpty(entry.Name))
                _fileSystem.FolderCreate(target);
            else
            {
                _fileSystem.EnsureParentFolderExists(target);
                entry.ExtractToFile(target, overwrite: true);
            }

            done++;
            if ((DateTime.UtcNow - lastLog).TotalSeconds >= ProgressLogSeconds)
            {
                _logger.LogDebug("Extraction progress: {Done}/{Total} entries", done, total);
                lastLog = DateTime.UtcNow;
            }
        }
    }

    // A ZIP starts with PK followed by 03 04, 05 06 (empty) or 07 08 (spanned)
    private bool IsZip(string path)
    {
        using var stream = _fileSystem.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        return stream.ReadAtLeast(header, 4, throwOnEndOfStream: false) == 4 &&
               header[0] == 0x50 && header[1] == 0x4B && header[2] is 0x03 or 0x05 or 0x07;
    }

    // The start of a download that is not an archive (an error page), for the log
    private string Head(string path)
    {
        using var reader = new StreamReader(_fileSystem.OpenRead(path));
        var buffer = new char[500];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }
}
