#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data;

namespace Registry.Web.Services.Jobs;

/// <summary>A dataset visited by a <see cref="DatasetSweep"/>.</summary>
public sealed record SweptDataset(string OrgSlug, string DsSlug, Guid InternalRef)
{
    /// <summary>"org/dataset", for log lines.</summary>
    public string Key => $"{OrgSlug}/{DsSlug}";
}

/// <summary>How a <see cref="DatasetSweep"/> went: datasets visited and datasets whose visit failed.</summary>
public readonly record struct DatasetSweepResult(int Datasets, int Failed)
{
    public int Succeeded => Datasets - Failed;
}

/// <summary>
/// The loop of the recurring dataset sweeps: one query, one dataset at a time, the token checked
/// between datasets, and a dataset whose visit fails logged on the job console and counted
/// without stopping the sweep. Cancellation is not a failure: it ends the sweep.
/// </summary>
public sealed class DatasetSweep
{
    private readonly RegistryContext _context;

    public DatasetSweep(RegistryContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// One dataset with its organization, as the sweeps see the datasets (untracked), or null when there is none:
    /// for the per-dataset work that runs next to a sweep (the pending-build flag, retry and refresh).
    /// </summary>
    public Task<Data.Models.Dataset?> FindAsync(string orgSlug, string dsSlug) =>
        _context.Datasets.AsNoTracking().Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.Organization.Slug == orgSlug && d.Slug == dsSlug);

    /// <param name="console">Where a failed visit is reported.</param>
    /// <param name="operation">What the sweep does, for the failure line ("Cleanup").</param>
    /// <param name="visit">The work on one dataset.</param>
    /// <param name="ct">Stops the sweep before the next dataset (and inside a visit that honours it).</param>
    public async Task<DatasetSweepResult> ForEachAsync(JobConsole console, string operation,
        Func<SweptDataset, CancellationToken, Task> visit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(visit);

        var datasets = await _context.Datasets
            .AsNoTracking()
            .Select(d => new SweptDataset(d.Organization.Slug, d.Slug, d.InternalRef))
            .ToArrayAsync(ct);

        var failed = 0;
        foreach (var dataset in datasets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await visit(dataset, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                console.Warning(ex, "{Operation} failed for {Dataset}: {Error}", operation, dataset.Key, ex.Message);
            }
        }

        return new DatasetSweepResult(datasets.Length, failed);
    }
}
