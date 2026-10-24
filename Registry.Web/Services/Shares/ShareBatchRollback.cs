using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Entry = Registry.Web.Data.Models.Entry;

namespace Registry.Web.Services.Shares;

/// <summary>Rolls share batches back: the batch ends first (a concurrent rollback sees it), then its files go, or its whole dataset.</summary>
public sealed class ShareBatchRollback
{
    private readonly RegistryContext _context;
    private readonly IDatasetAccessResolver _access;
    private readonly ShareBatchStore _batches;
    private readonly IDatasetsManager _datasetsManager;
    private readonly IEntryCommandService _entries;
    private readonly ILogger<ShareBatchRollback> _logger;

    public ShareBatchRollback(RegistryContext context, IDatasetAccessResolver access, ShareBatchStore batches, IDatasetsManager datasetsManager, IEntryCommandService entries, ILogger<ShareBatchRollback> logger)
    {
        _context = context;
        _access = access;
        _batches = batches;
        _datasetsManager = datasetsManager;
        _entries = entries;
        _logger = logger;
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

        if (deleteDataset)
            await _datasetsManager.Delete(org.Slug, ds.Slug);
        else
        {
            foreach (var entry in batch.Entries)
                await _entries.DeleteAsync(org.Slug, ds.Slug, [entry.Path]);
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
