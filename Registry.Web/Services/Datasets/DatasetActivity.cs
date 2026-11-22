using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks;

namespace Registry.Web.Services.Datasets;

/// <summary>
/// A dataset that a task is rewriting as a whole: <paramref name="Kind"/> says what the task does
/// (<see cref="DatasetActivity.Importing"/>, <see cref="DatasetActivity.Rescanning"/>), <paramref name="TaskId"/> is the
/// task and <paramref name="ProgressPercent"/> its progress (null while it cannot tell).
/// </summary>
public sealed record DatasetBusy(string Kind, string TaskId, int? ProgressPercent);

/// <summary>
/// Whether a dataset is busy: a task that rewrites its index and its files as a whole (the import of a dataset, the
/// rescan of its index) is queued or running on it. A busy dataset can only be deleted, and the delete stops the task
/// first; every other request on it is refused (<see cref="DatasetBusyException"/>), so nothing races with the task.
/// </summary>
public interface IDatasetActivity
{
    /// <summary>The busy state of dataset <paramref name="dsSlug"/> of <paramref name="orgSlug"/> (its newest locking task), or null.</summary>
    Task<DatasetBusy?> GetAsync(string orgSlug, string dsSlug, CancellationToken ct = default);

    /// <summary>
    /// The busy states of the datasets <paramref name="dsSlugs"/> of <paramref name="orgSlug"/>, in one query (a page of the
    /// dataset list): only the busy datasets are in the result, keyed by their slug.
    /// </summary>
    Task<IReadOnlyDictionary<string, DatasetBusy>> GetForOrgAsync(string orgSlug, IReadOnlyCollection<string> dsSlugs,
        CancellationToken ct = default);

    /// <summary>
    /// The ids of the locking tasks of dataset <paramref name="dsSlug"/> of <paramref name="orgSlug"/> that are queued or
    /// running: those the delete of the dataset stops at once.
    /// </summary>
    Task<IReadOnlyList<string>> ActiveTaskIdsAsync(string orgSlug, string dsSlug, CancellationToken ct = default);

    /// <exception cref="DatasetBusyException">The dataset is busy.</exception>
    Task EnsureNotBusyAsync(string orgSlug, string dsSlug, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class DatasetActivity : IDatasetActivity
{
    /// <summary>The busy kind of a dataset that a task is importing.</summary>
    public const string Importing = "importing";

    /// <summary>The busy kind of a dataset whose index a task is rescanning.</summary>
    public const string Rescanning = "rescanning";

    /// <summary>
    /// The tools that lock their dataset, with the busy kind they give it and what the refusal calls their work. Only these
    /// two rewrite the dataset as a whole: a task that adds one file (a file import, an archive extraction, the results of
    /// a processing node) leaves the dataset usable.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Kind, string Work)> LockingTools =
        new Dictionary<string, (string Kind, string Work)>(StringComparer.Ordinal)
        {
            [HeavyToolIds.ImportDataset] = (Importing, "import"),
            [HeavyToolIds.RescanIndex] = (Rescanning, "rescan")
        };

    private static readonly string[] LockingToolIds = [.. LockingTools.Keys];

    private readonly RegistryContext _context;

    public DatasetActivity(RegistryContext context)
    {
        _context = context;
    }

    /// <summary>The refusal of a request on dataset <paramref name="dsSlug"/> of <paramref name="orgSlug"/> while it is <paramref name="busy"/>.</summary>
    public static DatasetBusyException Refusal(string orgSlug, string dsSlug, DatasetBusy busy)
    {
        var work = LockingTools.Values.First(t => t.Kind == busy.Kind).Work;
        return new DatasetBusyException(
            $"Dataset '{orgSlug}/{dsSlug}' is {busy.Kind}: it can only be deleted until the {work} ends",
            busy.Kind, busy.TaskId);
    }

    public async Task<DatasetBusy?> GetAsync(string orgSlug, string dsSlug, CancellationToken ct = default) =>
        Newest(await ActiveOfDatasetAsync(orgSlug, dsSlug, ct)).GetValueOrDefault(dsSlug);

    public async Task<IReadOnlyDictionary<string, DatasetBusy>> GetForOrgAsync(string orgSlug,
        IReadOnlyCollection<string> dsSlugs, CancellationToken ct = default)
    {
        if (dsSlugs.Count == 0)
            return new Dictionary<string, DatasetBusy>();

        var slugs = dsSlugs.ToArray();
        var rows = await _context.JobIndices.AsNoTracking()
            .Where(j => j.OrgSlug == orgSlug && slugs.Contains(j.DsSlug))
            .Where(ActiveLocking)
            .Select(Row)
            .ToArrayAsync(ct);
        return Newest(Ordinal(rows, orgSlug, slugs));
    }

    public async Task<IReadOnlyList<string>> ActiveTaskIdsAsync(string orgSlug, string dsSlug,
        CancellationToken ct = default) =>
        [.. (await ActiveOfDatasetAsync(orgSlug, dsSlug, ct)).Select(r => r.JobId)];

    public async Task EnsureNotBusyAsync(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        if (await GetAsync(orgSlug, dsSlug, ct) is { } busy)
            throw Refusal(orgSlug, dsSlug, busy);
    }

    private sealed record ActiveRow(string JobId, string OrgSlug, string DsSlug, string ToolId, int? ProgressPercent,
        DateTime CreatedAtUtc);

    private static readonly Expression<Func<JobIndex, ActiveRow>> Row =
        j => new ActiveRow(j.JobId, j.OrgSlug, j.DsSlug, j.ToolId, j.ProgressPercent, j.CreatedAtUtc);

    // A task of a locking tool in an active state. The tools and the states are constants of the query, not bound
    // values: the plan is the same for every request, and the index (OrgSlug, DsSlug, ToolId, CurrentState) answers it
    // without reading the history of the dataset
    private static readonly Expression<Func<JobIndex, bool>> ActiveLocking = ActiveLockingPredicate();

    // The locking tasks of one dataset in an active state: one indexed lookup
    private async Task<ActiveRow[]> ActiveOfDatasetAsync(string orgSlug, string dsSlug, CancellationToken ct) =>
        Ordinal(await _context.JobIndices.AsNoTracking()
            .Where(j => j.OrgSlug == orgSlug && j.DsSlug == dsSlug)
            .Where(ActiveLocking)
            .Select(Row)
            .ToArrayAsync(ct), orgSlug, [dsSlug]);

    // ToolId in the locking tools and CurrentState in the active states, each as member == value || ...
    private static Expression<Func<JobIndex, bool>> ActiveLockingPredicate()
    {
        var job = Expression.Parameter(typeof(JobIndex), "j");

        Expression OneOf(string member, IEnumerable<string> values) => values
            .Select(v => (Expression)Expression.Equal(Expression.Property(job, member), Expression.Constant(v)))
            .Aggregate(Expression.OrElse);

        return Expression.Lambda<Func<JobIndex, bool>>(Expression.AndAlso(
            OneOf(nameof(JobIndex.ToolId), LockingToolIds),
            OneOf(nameof(JobIndex.CurrentState), TaskStateCatalog.Active)), job);
    }

    // The rows of these datasets, compared again ordinally in memory (the MariaDB collation matches "SOURCE" to "source"),
    // of a locking tool spelt as the catalog spells it
    private static ActiveRow[] Ordinal(IEnumerable<ActiveRow> rows, string orgSlug, IReadOnlyCollection<string> dsSlugs)
    {
        var wanted = dsSlugs.ToHashSet(StringComparer.Ordinal);
        return [.. rows.Where(r => string.Equals(r.OrgSlug, orgSlug, StringComparison.Ordinal) &&
                                   wanted.Contains(r.DsSlug) && LockingTools.ContainsKey(r.ToolId))];
    }

    // The newest locking task of each dataset
    private static Dictionary<string, DatasetBusy> Newest(IEnumerable<ActiveRow> rows) =>
        rows.GroupBy(r => r.DsSlug, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                var newest = g.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.JobId, StringComparer.Ordinal)
                    .First();
                return new DatasetBusy(LockingTools[newest.ToolId].Kind, newest.JobId, newest.ProgressPercent);
            }, StringComparer.Ordinal);
}
