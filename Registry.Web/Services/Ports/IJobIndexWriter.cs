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
}