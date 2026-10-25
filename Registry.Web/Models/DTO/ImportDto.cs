using System;
using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Import mode for dataset import operations
/// </summary>
public enum ImportMode
{
    /// <summary>
    /// Download the entire dataset as a ZIP archive (default, existing behavior)
    /// </summary>
    Archive,

    /// <summary>
    /// Download files individually in parallel, skipping files that already exist with the same hash
    /// </summary>
    ParallelFiles
}

public class ImportDatasetRequestDto
{
    public string SourceRegistryUrl { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string SourceOrganization { get; set; }
    public string SourceDataset { get; set; }
    public string DestinationOrganization { get; set; }
    public string DestinationDataset { get; set; }

    /// <summary>
    /// Import mode: Archive (default) downloads as ZIP, ParallelFiles downloads files individually
    /// </summary>
    public ImportMode Mode { get; set; } = ImportMode.Archive;
}

public class ImportOrganizationRequestDto
{
    public string SourceRegistryUrl { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string SourceOrganization { get; set; }
    public string DestinationOrganization { get; set; }

    /// <summary>
    /// Import mode: Archive (default) downloads as ZIP, ParallelFiles downloads files individually
    /// </summary>
    public ImportMode Mode { get; set; } = ImportMode.Archive;
}

public class ImportResultDto
{
    public ImportedItemDto[] ImportedItems { get; set; }
    public ImportErrorDto[] Errors { get; set; }

    /// <summary>
    /// Errors for individual files (only populated in ParallelFiles mode)
    /// </summary>
    public FileImportErrorDto[] FileErrors { get; set; }

    public long TotalSize { get; set; }
    public int TotalFiles { get; set; }

    /// <summary>
    /// Number of files skipped because they already existed with the same hash (ParallelFiles mode only)
    /// </summary>
    public int SkippedFiles { get; set; }

    public TimeSpan Duration { get; set; }
}

public class ImportedItemDto
{
    public string Organization { get; set; }
    public string Dataset { get; set; }
    public long Size { get; set; }
    public int FileCount { get; set; }
    public int SkippedFileCount { get; set; }
    public DateTime ImportedAt { get; set; }
}

public enum ImportPhase
{
    Authentication,
    Download,
    Save,
    General
}

public class ImportErrorDto
{
    public string Organization { get; set; }
    public string Dataset { get; set; }
    public string Message { get; set; }
    public ImportPhase Phase { get; set; }
}

/// <summary>
/// Error details for individual file import failures (ParallelFiles mode)
/// </summary>
public class FileImportErrorDto
{
    public string Organization { get; set; }
    public string Dataset { get; set; }
    public string FilePath { get; set; }
    public string Message { get; set; }
    public int RetryCount { get; set; }
}

/// <summary>
/// A remote import queued as a background job (<c>POST /sys/import-dataset</c> and
/// <c>/sys/import-organization</c> answer 202 with it, <c>Location</c> is the status URL).
/// </summary>
public class RemoteImportJobDto
{
    public string JobId { get; set; }

    /// <summary>The URL of <see cref="RemoteImportStatusDto"/> (<c>GET</c>; <c>DELETE</c> cancels the import).</summary>
    public string StatusUrl { get; set; }
}

/// <summary>The state of a remote import job and, once it is over, its outcome.</summary>
public class RemoteImportStatusDto
{
    public string JobId { get; set; }

    /// <summary>The Hangfire state: Enqueued, Processing, Succeeded, Failed, Deleted...</summary>
    public string State { get; set; }

    /// <summary>The result of a succeeded import (the body the former synchronous endpoint answered).</summary>
    public ImportResultDto Result { get; set; }

    /// <summary>The error of a failed job (an import that ran reports its errors in <see cref="Result"/>).</summary>
    public string Error { get; set; }
}
