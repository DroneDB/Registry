using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

/// <summary>The share batches: the running batch of a token (its owner or an administrator only), its readiness for a path, the batches of a dataset, a new batch, its end, its entries.</summary>
public sealed class ShareBatchStore
{
    private readonly RegistryContext _context;
    private readonly IAuthManager _authManager;
    private readonly IDatasetAccessResolver _access;
    private readonly IBatchTokenGenerator _batchTokenGenerator;
    private readonly TimeProvider _time;
    private readonly ILogger<ShareBatchStore> _logger;

    public ShareBatchStore(RegistryContext context, IAuthManager authManager, IDatasetAccessResolver access, IBatchTokenGenerator batchTokenGenerator, TimeProvider time, ILogger<ShareBatchStore> logger)
    {
        _context = context;
        _authManager = authManager;
        _access = access;
        _batchTokenGenerator = batchTokenGenerator;
        _time = time;
        _logger = logger;
    }

    public async Task<Batch> GetRunningAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new BadRequestException("Missing token");

        var batch = await _context.Batches
            .Include(x => x.Dataset.Organization)
            .Include(x => x.Entries)
            .FirstOrDefaultAsync(item => item.Token == token, ct);

        if (batch == null)
            throw new NotFoundException("Cannot find batch");

        await _access.EnsureAccessAsync(batch.Dataset, AccessType.Read);

        if (batch.Status != BatchStatus.Running)
            throw new BadRequestException("Only running batches can be rollbacked");

        var currentUserName = await _authManager.SafeGetCurrentUserName();

        if (!(await _authManager.IsUserAdmin() || batch.UserName == currentUserName))
            throw new BadRequestException("This batch does not belong to you");

        return batch;
    }

    public async Task<IsBatchReadyResult> IsReadyAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new BadRequestException("Missing token");

        var batch = await _context.Batches
            .Include(x => x.Dataset.Organization)
            .Include(x => x.Entries)
            .FirstOrDefaultAsync(item => item.Token == token, ct);

        if (batch == null)
        {
            _logger.LogDebug("Cannot find batch '{Token}'", token);
            return IsBatchReadyResult.NotReady;
        }

        if (batch.Status != BatchStatus.Running)
        {
            _logger.LogDebug("Cannot upload file to closed batch '{Token}'", token);
            return IsBatchReadyResult.NotReady;
        }

        var currentUserName = await _authManager.SafeGetCurrentUserName();

        if (!(await _authManager.IsUserAdmin() || batch.UserName == currentUserName))
        {
            _logger.LogDebug("The batch '{Token}' does not belong to you", token);
            return IsBatchReadyResult.NotReady;
        }

        return new IsBatchReadyResult(true, batch.Entries.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal));
    }

    public async Task<bool> IsPathAllowedAsync(string token, string path, CancellationToken ct = default)
    {
        var res = await IsReadyAsync(token, ct);

        if (!res.IsReady)
        {
            _logger.LogDebug("The batch '{Token}' is not ready", token);
            return false;
        }

        if (res.EntryPaths.Contains(path))
        {
            _logger.LogDebug("The batch '{Token}' already contains path '{Path}'", token, path);
            return false;
        }

        return true;
    }

    public async Task<BatchDto> GetBatchInfo(string token, CancellationToken ct = default)
    {
        var batch = await GetRunningAsync(token, ct);

        return new BatchDto
        {
            End = batch.End,
            Start = batch.Start,
            Token = batch.Token,
            UserName = batch.UserName,
            Status = batch.Status,
            Entries = from entry in batch.Entries
                select new BatchEntryDto
                {
                    Hash = entry.Hash,
                    Type = entry.Type,
                    Size = entry.Size,
                    AddedOn = entry.AddedOn,
                    Path = entry.Path
                }
        };
    }

    public async Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        var ds = access.Dataset;

        _logger.LogInformation("Listing batches of '{OrgSlug}/{DsSlug}'", orgSlug, dsSlug);

        // Read here, with the token, not while the response is serialized
        var batches = await (from batch in _context.Batches
                .Include(x => x.Entries)
                .Include(x => x.Dataset)
            where batch.Dataset.Id == ds.Id
            select new BatchDto
            {
                End = batch.End,
                Start = batch.Start,
                Token = batch.Token,
                UserName = batch.UserName,
                Status = batch.Status,
                Entries = from entry in batch.Entries
                    select new BatchEntryDto
                    {
                        Hash = entry.Hash,
                        Type = entry.Type,
                        Size = entry.Size,
                        AddedOn = entry.AddedOn,
                        Path = entry.Path
                    }
            }).ToListAsync(ct);

        return batches;
    }

    /// <summary>Starts a running batch on <paramref name="dataset"/> for the current user.</summary>
    public async Task<Batch> StartAsync(Dataset dataset)
    {
        var batch = new Batch
        {
            Dataset = dataset,
            Start = UtcNow(),
            Token = _batchTokenGenerator.GenerateToken(),
            UserName = await _authManager.SafeGetCurrentUserName(),
            Status = BatchStatus.Running
        };

        _logger.LogInformation("Adding new batch for user '{BatchUserName}' with token '{BatchToken}'",
            batch.UserName, batch.Token);

        await _context.Batches.AddAsync(batch);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Batch created, it is now possible to upload files");
        return batch;
    }

    /// <summary>Ends <paramref name="batch"/> with <paramref name="status"/> now, then loads its entries.</summary>
    public async Task CloseAsync(Batch batch, BatchStatus status)
    {
        ArgumentNullException.ThrowIfNull(batch);
        batch.Status = status;
        batch.End = UtcNow();
        await _context.SaveChangesAsync();

        await _context.Entry(batch).Collection(item => item.Entries).LoadAsync();
    }

    /// <summary>
    /// Shared tail of the share upload flow: records the entry the add just indexed on the batch
    /// (no second lookup: the add returns the indexed type, hash and size).
    /// </summary>
    public async Task<UploadResultDto> AddEntryAsync(Batch batch, string path, EntryDto info)
    {
        if (info == null)
            throw new BadRequestException(
                "Underlying object storage is not working correctly: cannot find object after adding it");

        var entry = new Entry
        {
            Type = info.Type,
            Hash = info.Hash,
            AddedOn = UtcNow(),
            Path = path,
            Size = info.Size,
            Batch = batch
        };

        await _context.AddAsync(entry);

        _logger.LogInformation("Entry added");

        await _context.SaveChangesAsync();

        _logger.LogInformation("Changes commited");

        return new UploadResultDto
        {
            Hash = entry.Hash,
            Size = entry.Size,
            Path = entry.Path
        };
    }

    // Chunks of a share upload live where the dataset's other uploads do (same volume as the dataset)
    // Batch and entry times are UTC: they were the server's local time, which the API then
    // served as UTC (Kind is lost in the database)
    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;
}
