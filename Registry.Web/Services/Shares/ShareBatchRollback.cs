using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Entry = Registry.Web.Data.Models.Entry;

namespace Registry.Web.Services.Shares;

/// <summary>
/// Rolls share batches back: the batch ends first (a concurrent rollback sees it), then its files go, or its whole
/// dataset. The files of a batch are the paths it created: a file it replaced stays (<see cref="Entry.Replaced"/>,
/// FR-XC-27). A dataset that the batch created goes only while it holds nothing but the batch's files.
/// </summary>
public sealed class ShareBatchRollback
{
    private readonly RegistryContext _context;
    private readonly IDatasetAccessResolver _access;
    private readonly ShareBatchStore _batches;
    private readonly IDatasetsManager _datasetsManager;
    private readonly IEntryCommandService _entries;
    private readonly IDdbManager _ddbManager;
    private readonly ILogger<ShareBatchRollback> _logger;

    public ShareBatchRollback(RegistryContext context, IDatasetAccessResolver access, ShareBatchStore batches, IDatasetsManager datasetsManager, IEntryCommandService entries, IDdbManager ddbManager, ILogger<ShareBatchRollback> logger)
    {
        _context = context;
        _access = access;
        _batches = batches;
        _datasetsManager = datasetsManager;
        _entries = entries;
        _ddbManager = ddbManager;
        _logger = logger;
    }

    /// <summary>
    /// Whether which files <paramref name="batch"/> replaced is unknown: a batch that started before the upgrade that
    /// records it (<see cref="Batch.LastActivity"/> is null, every <see cref="Entry.Replaced"/> false) on a dataset it
    /// did not create. Its entries may name files that were there before it: a rollback deletes none of them (FR-XC-27).
    /// </summary>
    internal static bool ReplacedUnknown(Batch batch) => batch.LastActivity == null && !batch.CreatedDataset;

    /// <summary>
    /// The paths a rollback of <paramref name="batch"/> deletes: the ones it created, not the ones it replaced; none
    /// when that is unknown (<see cref="ReplacedUnknown"/>).
    /// </summary>
    internal static List<string> OwnedPaths(Batch batch) => ReplacedUnknown(batch) ? [] : OwnedPaths(batch.Entries);

    /// <summary>The paths a rollback of <paramref name="entries"/> deletes: the ones the batch created, not the ones it replaced.</summary>
    internal static List<string> OwnedPaths(IEnumerable<Entry> entries) =>
        entries.Where(e => !e.Replaced).Select(e => e.Path).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Whether the dataset of <paramref name="ddb"/> holds only <paramref name="owned"/> (and the folders above them):
    /// then deleting the dataset deletes nothing that is not the batch's. An entry added outside the batch keeps it.
    /// </summary>
    internal static bool HoldsOnly(IDDB ddb, IReadOnlyCollection<string> owned)
    {
        var files = new HashSet<string>(owned, StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in owned)
        {
            for (var cut = path.LastIndexOf('/'); cut > 0; cut = path.LastIndexOf('/', cut - 1))
                folders.Add(path[..cut]);
        }

        return ddb.FindSummaries(EntryQuery.All)
            .All(e => files.Contains(e.Path) || (e.Type == EntryType.Directory && folders.Contains(e.Path)));
    }

    public async Task RollbackAsync(Batch batch, bool deleteDataset = false)
    {
        _logger.LogInformation("Rolling back batch '{BatchToken}'", batch.Token);

        await _context.Entry(batch).Reference(item => item.Dataset).LoadAsync();

        var ds = batch.Dataset;

        await _context.Entry(ds).Reference(item => item.Organization).LoadAsync();
        var org = ds.Organization;

        // Check the access the rollback needs (dropping the dataset or deleting its entries) before the status
        // change below is committed: a denied rollback must leave the batch running
        var requiredAccess = deleteDataset ? AccessType.Delete : AccessType.Write;
        await _access.EnsureAccessAsync(ds, requiredAccess);

        // I know we will have concurrency problems here because we could be in the middle of the rollback when another client calls for another one
        // To mitigate this issue we are going to commit the status change of the batch as soon as possible

        await _batches.CloseAsync(batch, BatchStatus.Rolledback);

        if (ReplacedUnknown(batch) && batch.Entries.Count > 0)
            _logger.LogWarning("Batch '{BatchToken}' on {OrgSlug}/{DsSlug} started before replaced files were recorded: " +
                               "its {Count} file(s) stay (FR-XC-27)", batch.Token, org.Slug, ds.Slug, batch.Entries.Count);

        var owned = OwnedPaths(batch);
        if (deleteDataset && !HoldsOnly(_ddbManager.Get(org.Slug, ds.InternalRef), owned))
        {
            _logger.LogWarning("The dataset {OrgSlug}/{DsSlug} that batch '{BatchToken}' created holds entries added " +
                               "outside the batch: it is kept, only the batch's files go", org.Slug, ds.Slug, batch.Token);
            deleteDataset = false;
        }

        if (deleteDataset)
            await _datasetsManager.Delete(org.Slug, ds.Slug);
        else
        {
            foreach (var path in owned)
                await _entries.DeleteAsync(org.Slug, ds.Slug, [path]);
        }
    }

    /// <summary>Rolls back the running batches of <paramref name="dataset"/> (a new batch replaces them).</summary>
    public async Task RollbackRunningAsync(Dataset dataset)
    {
        // A new batch replaces the running ones: only who may write to the dataset may do that
        await _access.EnsureAccessAsync(dataset, AccessType.Write);

        _logger.LogInformation("Checking for running batches");
        await _context.Entry(dataset).Collection(item => item.Batches).LoadAsync();

        var runningBatches = dataset.Batches.Where(item => item.End == null).ToArray();

        if (runningBatches.Length != 0)
        {
            _logger.LogInformation(
                "Found '{RunningBatchesCount}' running batch(es), stopping and rolling back before starting a new one",
                runningBatches.Length);

            foreach (var b in runningBatches)
                await RollbackAsync(b);
        }
    }
}
