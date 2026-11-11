using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Ports.Import;

/// <summary>
/// Abstraction over an external data source that can be probed for reachability/credentials and
/// fetched (files written directly into the dataset folder, ready for DDB indexing).
/// Exactly one implementation per <see cref="SourceType"/> (SOLID / open-closed).
/// Implementations ALWAYS receive DECRYPTED parameters; credential encrypt/decrypt is the caller's
/// responsibility (the manager at the persist boundary, the tool at execute time). Adapters never
/// call <see cref="IImportCredentialProtector"/>.
/// </summary>
public interface IImportSource
{
    /// <summary>Kebab-case identifier matching the frontend source type IDs (e.g. "registry").</summary>
    string SourceType { get; }

    /// <summary>
    /// Verifies connectivity and credentials without transferring data. Must be fast (bounded by
    /// the configured connect timeout). Receives decrypted params.
    /// </summary>
    /// <param name="parameters">Source-specific parameters (decrypted).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A probe result describing reachability and best-effort size/count hints.</returns>
    Task<ImportSourceProbe> ProbeAsync(JsonElement parameters, CancellationToken ct);

    /// <summary>
    /// Fetches the source files into <paramref name="destFolder"/> (the dataset folder) through the task's write
    /// record (<paramref name="writes"/>): each file and folder is recorded before it is created, a file is never
    /// written over one that is not the task's (it is skipped), and a cancelled or failed import is rolled back from
    /// the record by the task. Reports incremental progress (including <see cref="ImportProgress.BytesSoFar"/>). Must
    /// honour cancellation promptly; the tool enforces the size/quota budget by cancelling when the reported bytes
    /// exceed it. Receives decrypted params.
    /// </summary>
    /// <param name="parameters">Source-specific parameters (decrypted).</param>
    /// <param name="destFolder">Absolute path of the dataset folder to write files into.</param>
    /// <param name="writes">The task's write record, for every file and folder the source creates.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when all files have been fetched.</returns>
    Task FetchAsync(JsonElement parameters, string destFolder, IImportWriteRecord writes,
        IProgress<ImportProgress> progress, CancellationToken ct);
}
