using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// What a task tells a dataset when entries left it (its rollback, the sources Merge deletes): the entry change
/// notification of the entry commands (<see cref="EntryChangeNotifier.EntriesLeftAsync"/>: OGC catalog, dataset
/// thumbnail, waits of builds) on the web host, the dataset caches (<see cref="IDatasetCacheInvalidator"/>) on a
/// processing node, which has no notifier. The entries are gone already: a failure is logged, never thrown.
/// </summary>
public sealed class TaskEntryChanges
{
    private readonly IDatasetCacheInvalidator _caches;
    private readonly RegistryContext _db;
    private readonly ILogger<TaskEntryChanges> _log;
    private readonly EntryChangeNotifier? _changes;

    public TaskEntryChanges(IDatasetCacheInvalidator caches, RegistryContext db, ILogger<TaskEntryChanges> log,
        EntryChangeNotifier? changes = null)
    {
        _caches = caches;
        _db = db;
        _log = log;
        _changes = changes;
    }

    /// <summary>Entries at <paramref name="paths"/> left the dataset <paramref name="dataset"/> (found by its folder id).</summary>
    public async Task EntriesLeftAsync(DatasetKey dataset, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            return;

        try
        {
            var dsSlug = await _db.Datasets.AsNoTracking().Where(d => d.InternalRef == dataset.InternalRef)
                .Select(d => d.Slug).FirstOrDefaultAsync();
            if (dsSlug != null)
                await EntriesLeftAsync(dataset.OrgSlug, dsSlug, paths);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cannot announce the {Count} entries that left {OrgSlug}/{InternalRef}", paths.Count,
                dataset.OrgSlug, dataset.InternalRef);
        }
    }

    /// <summary>Entries at <paramref name="paths"/> left the dataset <paramref name="orgSlug"/>/<paramref name="dsSlug"/>.</summary>
    public async Task EntriesLeftAsync(string orgSlug, string dsSlug, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            return;

        try
        {
            if (_changes != null)
                await _changes.EntriesLeftAsync(orgSlug, dsSlug, paths);
            else
                await _caches.InvalidateAllDatasetCachesAsync(orgSlug, dsSlug);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cannot announce the {Count} entries that left {OrgSlug}/{DsSlug}", paths.Count,
                orgSlug, dsSlug);
        }
    }
}
