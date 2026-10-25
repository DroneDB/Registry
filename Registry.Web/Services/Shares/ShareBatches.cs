using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Shares;

/// <summary>The life of a share batch: its start (on the dataset <see cref="ShareTargets"/> resolves), its commit, its rollback.</summary>
public sealed class ShareBatches
{
    private readonly ShareBatchStore _batches;
    private readonly ShareBatchRollback _rollback;
    private readonly ShareTargets _targets;
    private readonly IAuthManager _authManager;
    private readonly IStorageQuota _quota;
    private readonly ILogger<ShareBatches> _logger;

    public ShareBatches(ShareBatchStore batches, ShareBatchRollback rollback, ShareTargets targets, IAuthManager authManager, IStorageQuota quota, ILogger<ShareBatches> logger)
    {
        _batches = batches;
        _rollback = rollback;
        _targets = targets;
        _authManager = authManager;
        _quota = quota;
        _logger = logger;
    }

    public async Task<ShareInitResultDto> Initialize(ShareInitDto parameters, CancellationToken ct = default)
    {
        if (parameters == null)
            throw new BadRequestException("Invalid parameters");

        var currentUser = await _authManager.GetCurrentUser();
        if (currentUser == null)
            throw new UnauthorizedException("Invalid user");

        // Check if user has enough space to upload any file
        await _quota.CheckCurrentUserStorage();

        // Honoured until the target (which may be a new dataset) and the batch are written
        ct.ThrowIfCancellationRequested();
        var dataset = await _targets.ResolveAsync(parameters, currentUser);
        var batch = await _batches.StartAsync(dataset);

        return new ShareInitResultDto
        {
            Token = batch.Token
        };
    }

    public async Task<CommitResultDto> Commit(string token, CancellationToken ct = default)
    {
        var batch = await _batches.GetRunningAsync(token, ct);

        _logger.LogInformation("Committing batch '{Token}' @ {BatchEndDate} {BatchEndTime}", token,
            batch.End?.ToLongDateString(), batch.End?.ToLongTimeString());

        await _batches.CloseAsync(batch, BatchStatus.Committed);

        _logger.LogInformation("Batch '{Token}' committed", token);

        return new CommitResultDto
        {
            // NOTE: This url structure is so buried in the code that it's hard to find it, we should consider a better way to expose it
            Url = $"/r/{batch.Dataset.Organization.Slug}/{batch.Dataset.Slug}",
            Tag = new TagDto(batch.Dataset.Organization.Slug, batch.Dataset.Slug)
        };
    }

    public async Task Rollback(string token, CancellationToken ct = default)
    {
        var batch = await _batches.GetRunningAsync(token, ct);

        _logger.LogInformation("Rolling back  batch '{Token}'", token);

        await _rollback.RollbackAsync(batch, true);
    }
}
