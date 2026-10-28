#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Import;
using Registry.Web.Utilities;

namespace Registry.Web.Services.RemoteImport;

/// <summary>
/// <see cref="ImportMode.ParallelFiles"/>: the files of the remote dataset are downloaded one by one,
/// <see cref="ImportSettings.RegistryDownloadConcurrency"/> at a time, and added to the local index as they arrive
/// (one at a time: the index has one writer). A file already indexed with the source hash is skipped, so an import
/// can be run again to resume; a file that fails is reported and the others go on. The dataset is rebuilt when
/// something was downloaded.
/// </summary>
public sealed class ParallelFilesImportStrategy : IRemoteImportStrategy
{
    private readonly IRemoteRegistryClient _remote;
    private readonly ImportDestinations _destinations;
    private readonly IFileSystem _fileSystem;
    private readonly DatasetPathPolicy _paths;
    private readonly ImportSettings _settings;
    private readonly ILogger<ParallelFilesImportStrategy> _logger;

    public ParallelFilesImportStrategy(IRemoteRegistryClient remote, ImportDestinations destinations,
        IFileSystem fileSystem, DatasetPathPolicy paths, ImportSettings settings,
        ILogger<ParallelFilesImportStrategy> logger)
    {
        _remote = remote;
        _destinations = destinations;
        _fileSystem = fileSystem;
        _paths = paths;
        _settings = settings;
        _logger = logger;
    }

    public ImportMode Mode => ImportMode.ParallelFiles;

    public async Task ImportAsync(RemoteDataset dataset, ImportReport report, CancellationToken ct)
    {
        var remoteFiles = await _remote.ListFilesAsync(dataset.RegistryUrl, dataset.AuthToken,
            dataset.SourceOrganization, dataset.SourceDataset, ct);
        if (remoteFiles.Length == 0)
        {
            report.Error(dataset, "No files found in remote dataset", ImportPhase.Download);
            return;
        }

        _logger.LogInformation("Importing {FileCount} files ({Size:N0} bytes) of {Org}/{Ds} into {DestOrg}/{DestDs}",
            remoteFiles.Length, remoteFiles.Sum(f => f.Size), dataset.SourceOrganization, dataset.SourceDataset,
            dataset.DestinationOrganization, dataset.DestinationDataset);

        var destination = await _destinations.GetOrCreateAsync(dataset, ct);

        // The remote is untrusted: every path is checked before anything is downloaded (a rooted or ".." path
        // fails the dataset), the reserved ones (".ddb", ".uploads") are skipped
        var targets = _paths.ResolveUntrustedPaths(destination.Folder, remoteFiles, e => e.Path);
        _fileSystem.FolderCreate(destination.Folder);

        var ddb = _destinations.Open(destination);
        var transfer = new Transfer(dataset, ddb, IndexedHashes(ddb), report);

        await Parallel.ForEachAsync(targets,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _settings.RegistryDownloadConcurrency), CancellationToken = ct },
            async (target, token) => await ImportFileAsync(transfer, target.Item, target.LocalPath, token));

        _logger.LogInformation("Imported {Org}/{Ds}: {Downloaded} downloaded ({Bytes:N0} bytes), {Skipped} unchanged, " +
                               "{Failed} failed", dataset.SourceOrganization, dataset.SourceDataset, transfer.Downloaded,
            transfer.DownloadedBytes, transfer.Skipped, transfer.Failed);

        if (transfer.Downloaded > 0)
            await _destinations.QueueBuildAsync(destination, ddb);

        report.Imported(destination, transfer.DownloadedBytes + transfer.SkippedBytes,
            transfer.Downloaded + transfer.Skipped, transfer.Skipped);
    }

    private async Task ImportFileAsync(Transfer transfer, EntryDto entry, string localPath, CancellationToken ct)
    {
        if (_fileSystem.Exists(localPath) && transfer.IsIndexed(entry))
        {
            transfer.Skip(entry);
            return;
        }

        var result = await _remote.DownloadFileAsync(transfer.Dataset.RegistryUrl, transfer.Dataset.AuthToken,
            transfer.Dataset.SourceOrganization, transfer.Dataset.SourceDataset, entry.Path, localPath, ct);

        // The download reports a cancellation as a failure: the import stops, it does not report every file
        ct.ThrowIfCancellationRequested();

        if (!result.Success)
        {
            _logger.LogWarning("Cannot download {Path}: {Error}", entry.Path, result.ErrorMessage);
            transfer.Fail(entry, result.ErrorMessage, result.RetryCount);
            return;
        }

        transfer.Index(entry, result);
    }

    // The indexed files and their hashes, to skip the unchanged ones
    private static Dictionary<string, string> IndexedHashes(IDDB ddb) =>
        ddb.FindSummaries(EntryQuery.All)
            .Where(e => !string.IsNullOrEmpty(e.Hash))
            .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().FileHash(), StringComparer.OrdinalIgnoreCase);

    // The state of one dataset transfer, shared by the download workers: the index has one writer at a time
    private sealed class Transfer(RemoteDataset dataset, IDDB ddb, Dictionary<string, string> indexed,
        ImportReport report)
    {
        private readonly object _lock = new();

        public RemoteDataset Dataset { get; } = dataset;
        public int Downloaded { get; private set; }
        public long DownloadedBytes { get; private set; }
        public int Skipped { get; private set; }
        public long SkippedBytes { get; private set; }
        public int Failed { get; private set; }

        public bool IsIndexed(EntryDto entry) =>
            indexed.TryGetValue(entry.Path, out var hash) && hash == entry.Hash;

        public void Skip(EntryDto entry)
        {
            lock (_lock)
            {
                Skipped++;
                SkippedBytes += entry.Size;
            }
        }

        public void Fail(EntryDto entry, string? message, int retryCount)
        {
            lock (_lock)
                Failed++;
            report.FileError(Dataset, entry.Path, message, retryCount);
        }

        public void Index(EntryDto entry, Registry.Common.HttpHelper.DownloadResult download)
        {
            lock (_lock)
            {
                try
                {
                    ddb.AddRaw(entry.Path);
                }
                catch (DdbException ex)
                {
                    Failed++;
                    report.FileError(Dataset, entry.Path, $"DDB add failed: {ex.Message}", download.RetryCount);
                    return;
                }

                Downloaded++;
                DownloadedBytes += download.BytesDownloaded;
            }
        }
    }
}
