#nullable enable
using System;
using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Result of a build cleanup operation.
/// When the cleanup runs synchronously, Datasets contains per-dataset details.
/// When it is enqueued as a background job, JobId is populated and Datasets is empty.
/// </summary>
public class CleanupBuildResultDto
{
    /// <summary>
    /// Per-dataset cleanup outcomes. Key is "{orgSlug}/{datasetSlug}".
    /// </summary>
    public Dictionary<string, DatasetCleanupBuildDto> Datasets { get; set; } = new();

    /// <summary>
    /// Errors encountered for individual datasets that could not be cleaned.
    /// Key is "{orgSlug}/{datasetSlug}", value is the error message.
    /// </summary>
    public Dictionary<string, string> Errors { get; set; } = new();

    /// <summary>
    /// Identifier of the background job, when the operation was enqueued.
    /// </summary>
    public string? JobId { get; set; }

    /// <summary>
    /// True when the operation has been enqueued as a background job.
    /// </summary>
    public bool Async { get; set; }
}

public class DatasetCleanupBuildDto
{
    /// <summary>Paths of entries removed from the index.</summary>
    public string[] RemovedEntries { get; set; } = Array.Empty<string>();

    /// <summary>Hashes of orphaned build artifacts removed.</summary>
    public string[] RemovedBuilds { get; set; } = Array.Empty<string>();
}
