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
}