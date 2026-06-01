#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Services.Ports;

public interface IJobIndexWriter
{
    Task UpsertOnEnqueueAsync(string jobId, IndexPayload meta, DateTime createdAtUtc, string? methodDisplay, CancellationToken ct = default);
    Task UpdateStateAsync(string jobId, string newState, DateTime changedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// Deletes all JobIndex records with terminal states (Succeeded, Failed, Deleted)
    /// whose LastStateChangeUtc is before the specified cutoff date.
    /// </summary>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteTerminalBeforeAsync(DateTime cutoffUtc, CancellationToken ct = default);

    /// <summary>Updates incremental progress, phase message and truncated log tail for a task.</summary>
    Task UpdateProgressAsync(string jobId, int? percent, string? phaseMessage,
        string? logTailJson, DateTime updatedAtUtc, CancellationToken ct = default);

    /// <summary>Persists produced-artifact metadata (size + SHA-256) for a task.</summary>
    Task UpdateArtifactAsync(string jobId, long sizeBytes, string? sha256, CancellationToken ct = default);

    /// <summary>Records the exception type name when a task failed.</summary>
    Task UpdateErrorAsync(string jobId, string errorType, CancellationToken ct = default);
}