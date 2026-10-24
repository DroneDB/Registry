#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.RemoteImport;

/// <summary>
/// The outcome of a remote import as it goes: the datasets imported, the errors of a dataset or of the whole import,
/// the files that failed. Safe to report from the download workers of a dataset.
/// </summary>
public sealed class ImportReport
{
    private readonly TimeProvider _time;
    private readonly List<ImportedItemDto> _imported = [];
    private readonly List<ImportErrorDto> _errors = [];
    private readonly List<FileImportErrorDto> _fileErrors = [];
    private readonly object _lock = new();

    public ImportReport(TimeProvider time) => _time = time;

    /// <summary>A dataset imported into <paramref name="destination"/>.</summary>
    public void Imported(ImportDestination destination, long size, int files, int skippedFiles = 0)
    {
        lock (_lock)
            _imported.Add(new ImportedItemDto
            {
                Organization = destination.OrganizationSlug,
                Dataset = destination.DatasetSlug,
                Size = size,
                FileCount = files,
                SkippedFileCount = skippedFiles,
                ImportedAt = _time.GetUtcNow().UtcDateTime
            });
    }

    /// <summary>An error of the import (<paramref name="dataset"/> null) or of one source dataset.</summary>
    public void Error(string organization, string? dataset, string message, ImportPhase phase)
    {
        lock (_lock)
            _errors.Add(new ImportErrorDto
            {
                Organization = organization,
                Dataset = dataset,
                Message = message,
                Phase = phase
            });
    }

    /// <summary>An error of the source dataset <paramref name="dataset"/>.</summary>
    public void Error(RemoteDataset dataset, string message, ImportPhase phase) =>
        Error(dataset.SourceOrganization, dataset.SourceDataset, message, phase);

    /// <summary>A file of <paramref name="dataset"/> that was not imported.</summary>
    public void FileError(RemoteDataset dataset, string path, string? message, int retryCount)
    {
        lock (_lock)
            _fileErrors.Add(new FileImportErrorDto
            {
                Organization = dataset.DestinationOrganization,
                Dataset = dataset.DestinationDataset,
                FilePath = path,
                Message = message,
                RetryCount = retryCount
            });
    }

    public ImportResultDto ToResult(TimeSpan duration)
    {
        lock (_lock)
            return new ImportResultDto
            {
                ImportedItems = [.. _imported],
                Errors = [.. _errors],
                FileErrors = [.. _fileErrors],
                TotalSize = _imported.Sum(i => i.Size),
                TotalFiles = _imported.Sum(i => i.FileCount),
                SkippedFiles = _imported.Sum(i => i.SkippedFileCount),
                Duration = duration
            };
    }
}
