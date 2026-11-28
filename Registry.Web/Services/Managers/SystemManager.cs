using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Objects;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// System-level data maintenance: expired upload batches, empty datasets, the visibility migration. The other
/// system operations have their own services (slug repair, report, build maintenance, remote import).
/// </summary>
public class SystemManager
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly IEntryCommandService _entries;
    private readonly ShareOptions _share;
    private readonly TimeProvider _time;
    private readonly IDatasetListingInvalidator _listings;
    private readonly ILogger<SystemManager> _logger;

    public SystemManager(RegistryContext context, IDdbManager ddbManager, IEntryCommandService entries,
        IOptions<ShareOptions> share, TimeProvider time, IDatasetListingInvalidator listings,
        ILogger<SystemManager> logger)
    {
        _context = context;
        _ddbManager = ddbManager;
        _entries = entries;
        _share = share.Value;
        _time = time;
        _listings = listings;
        _logger = logger;
    }

    // The maintenance commands honour the token while they select their targets; the per-dataset work then
    // runs to completion: a disconnect never leaves one dataset half done
    /// <summary>Removes the datasets whose index is empty.</summary>
    public async Task<CleanupDatasetResultDto> CleanupEmptyDatasets(CancellationToken ct = default)
    {
        var datasets = await _context.Datasets.Include(ds => ds.Organization).ToArrayAsync(ct);

        _logger.LogInformation("Found {DatasetsCount} with objects count zero", datasets.Length);

        var deleted = new List<string>();
        var notDeleted = new List<CleanupDatasetErrorDto>();

        foreach (var ds in datasets)
        {
            _logger.LogInformation("Analyzing dataset {OrgSlug}/{DsSlug}", ds.Organization.Slug, ds.Slug);

            try
            {
                // Check if objects count is ok
                var ddb = _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);

                if (ddb.IsEmpty())
                {
                    _context.Remove(ds);
                    await _context.SaveChangesAsync(CancellationToken.None);

                    deleted.Add(ds.Slug);
                    _logger.LogInformation("Deleted");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot remove dataset '{DsSlug}'", ds.Slug);
                notDeleted.Add(new CleanupDatasetErrorDto
                {
                    Dataset = ds.Slug,
                    Organization = ds.Organization.Slug,
                    Message = ex.Message
                });
            }
        }

        return new CleanupDatasetResultDto
        {
            RemoveDatasetErrors = [.. notDeleted],
            RemovedDatasets = [.. deleted]
        };

    }

    /// <summary>Gives a visibility to the datasets that still have only the legacy public flag.</summary>
    public async Task<IEnumerable<MigrateVisibilityEntryDTO>> MigrateVisibility(CancellationToken ct = default)
    {
        var query = await (from ds in _context.Datasets.Include(ds => ds.Organization)
            select new { ds = ds.Slug, ds.InternalRef, Org = ds.Organization.Slug }).ToArrayAsync(ct);

        _logger.LogInformation("Migrating to visibility {DatasetsCount} datasets", query.Length);

        var res = new List<MigrateVisibilityEntryDTO>();

        foreach (var pair in query)
        {
            var ddb = _ddbManager.Get(pair.Org, pair.InternalRef);
            var meta = ddb.Meta.GetSafe();

            if (meta.Visibility.HasValue) continue;

            var isPublic = meta.IsPublic;

            meta.Visibility = isPublic switch
            {
                null => Visibility.Private,
                true => Visibility.Unlisted,
                false => Visibility.Private
            };

            // Its cached visibility, and the listings that depend on it
            await _listings.VisibilityChangedAsync(pair.Org, pair.InternalRef, CancellationToken.None);

            res.Add(new MigrateVisibilityEntryDTO
            {
                IsPublic = isPublic,
                DatasetSlug = pair.ds,
                OrganizationSlug = pair.Org,
                Visibility = meta.Visibility.Value
            });
        }

        return res;
    }

    /// <summary>Removes the committed upload batches and the expired ones (with their files).</summary>
    public async Task<CleanupBatchesResultDto> CleanupBatches(CancellationToken ct = default)
    {
        // Batch entry times are UTC (rows written by earlier releases hold the server's local time)
        var expiration = _time.GetUtcNow().UtcDateTime - _share.UploadBatchTimeout;

        // I'm scared
        var toRemove = await (from batch in _context.Batches
                .Include(b => b.Dataset.Organization)
                .Include(b => b.Entries)
            where batch.Status == BatchStatus.Committed ||
                  ((batch.Status == BatchStatus.Rolledback || batch.Status == BatchStatus.Running) &&
                   batch.Entries.Max(entry => entry.AddedOn) < expiration)
            select batch).ToArrayAsync(ct);

        var removed = new List<RemovedBatchDto>();
        var errors = new List<RemoveBatchErrorDto>();

        foreach (var batch in toRemove)
        {
            var ds = batch.Dataset;
            var org = ds.Organization;

            try
            {
                // Remove intermediate files
                if (batch.Status is BatchStatus.Rolledback or BatchStatus.Running)
                {
                    var entries = batch.Entries.ToArray();

                    foreach (var entry in entries)
                    {
                        _logger.LogInformation("Deleting '{EntryPath}' of '{OrgSlug}/{DsSlug}'", entry.Path, org.Slug, ds.Slug);
                        await _entries.DeleteAsync(org.Slug, ds.Slug, [entry.Path], CancellationToken.None);
                    }

                    var ddb = _ddbManager.Get(org.Slug, ds.InternalRef);

                    // Remove empty ddb
                    if (ddb.IsEmpty())
                        _ddbManager.Delete(org.Slug, ds.InternalRef);

                }

                _context.Batches.Remove(batch);

                await _context.SaveChangesAsync(CancellationToken.None);

                removed.Add(new RemovedBatchDto
                {
                    Status = batch.Status,
                    Start = batch.Start,
                    End = batch.End,
                    Token = batch.Token,
                    UserName = batch.UserName,
                    Dataset = ds.Slug,
                    Organization = org.Slug
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot remove batch '{BatchToken}'", batch.Token);
                errors.Add(new RemoveBatchErrorDto
                {
                    Message = ex.Message,
                    Token = batch.Token,
                    Dataset = ds.Slug,
                    Organization = org.Slug
                });
            }
        }

        return new CleanupBatchesResultDto
        {
            RemovedBatches = [.. removed],
            RemoveBatchErrors = [.. errors]
        };

    }
}
