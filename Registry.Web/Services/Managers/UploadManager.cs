#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Uploads;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Default <see cref="IUploadManager"/>. The optional collaborators are wired in production; a
/// direct construction without them (tests) indexes with AddRaw, keeps no receipts and skips
/// thumbnail pre-generation.
/// </summary>
public sealed partial class UploadManager : IUploadManager
{
    private readonly ILogger<UploadManager> _logger;
    private readonly IAuthManager _authManager;
    private readonly IDatasetAccessResolver _access;
    private readonly IDdbManager _ddbManager;
    private readonly IFileSystem _fs;
    private readonly int _defaultThumbnailSize;
    private readonly ImportSettings _import;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly BuildPendingService _buildPendingService;
    private readonly IUploadQuotaGuard _quota;
    private readonly IUploadReceiptStore? _receipts;
    private readonly IDatasetIndexQueue? _indexQueue;
    private readonly IDatasetChangeCoalescer _changes;
    private readonly IThumbnailPrecomputeQueue? _thumbnails;
    private readonly DatasetPathPolicy _paths;
    private readonly IScratchFileProvider _scratch;

    public UploadManager(ILogger<UploadManager> logger, IAuthManager authManager,
        IDatasetAccessResolver access, IDdbManager ddbManager, IFileSystem fs, IOptions<ThumbnailOptions> thumbnailOptions,
        IOptions<ImportSettings> importSettings,
        IBackgroundJobsProcessor backgroundJob, BuildPendingService buildPendingService, IUploadQuotaGuard quota,
        IDatasetChangeCoalescer changes, DatasetPathPolicy paths, IScratchFileProvider scratch,
        IUploadReceiptStore? receipts = null,
        IDatasetIndexQueue? indexQueue = null, IThumbnailPrecomputeQueue? thumbnails = null)
    {
        _logger = logger;
        _authManager = authManager;
        _access = access;
        _ddbManager = ddbManager;
        _fs = fs;
        _defaultThumbnailSize = thumbnailOptions.Value.DefaultThumbnailSize;
        _import = importSettings.Value;
        _backgroundJob = backgroundJob;
        _buildPendingService = buildPendingService;
        _quota = quota;
        _receipts = receipts;
        _indexQueue = indexQueue;
        _changes = changes;
        _thumbnails = thumbnails;
        _paths = paths;
        _scratch = scratch;
    }

    // Client-generated ids: GUIDs or similar tokens, never anything that could shape a cache key
    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex UploadIdPattern();

    public static bool IsValidUploadId(string? uploadId) => uploadId != null && UploadIdPattern().IsMatch(uploadId);

    public async Task<UploadRequestContext> BeginAsync(string orgSlug, string dsSlug, long? expectedBytes = null,
        string? uploadId = null)
    {
        if (uploadId != null && !IsValidUploadId(uploadId))
            throw new BadRequestException("Invalid upload id: use 8 to 64 letters, digits, '-' or '_'");

        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write);
        var ds = access.Dataset;
        var ddb = access.Ddb;

        var user = await _authManager.GetCurrentUser();
        var ctx = new UploadRequestContext(orgSlug, dsSlug, ds, ddb, user, uploadId);

        try
        {
            if (uploadId != null && _receipts != null)
            {
                var receipt = await _receipts.GetAsync(orgSlug, dsSlug, uploadId);
                switch (receipt?.State)
                {
                    case UploadReceiptState.Done:
                        ctx.Receipt = receipt;
                        return ctx;
                    case UploadReceiptState.Receiving or UploadReceiptState.Committing:
                        throw new ConflictException($"Upload '{uploadId}' is still in progress");
                }

                await _receipts.SetAsync(orgSlug, dsSlug, uploadId,
                    new UploadReceipt(UploadReceiptState.Receiving, null, null, null, DateTime.UtcNow));
            }

            // Content-Length is known at byte 0: refuse an upload over quota before it is sent
            if (expectedBytes is > 0)
                ctx.Reservation = await _quota.ReserveAsync(QuotaUserId(user), expectedBytes.Value);

            if (_indexQueue != null)
                ctx.Ticket = _indexQueue.BeginUpload(ctx.Key);

            return ctx;
        }
        catch (Exception ex)
        {
            await FailAsync(ctx, ex);
            ctx.Dispose();
            throw;
        }
    }

    public string ValidatePath(UploadRequestContext ctx, string path)
    {
        // libddb's path rule (NUL, absolute, "..", reserved), then the extension policy: refused
        // before anything is written into the dataset
        var normalized = _paths.Normalize(path);
        if (!_import.IsExtensionAllowed(normalized))
            throw new ExtensionBlockedException(normalized, _import, _import.AllowedFileExtensions.Length > 0);
        return normalized;
    }

    public async Task<StreamedFile> StreamAsync(UploadRequestContext ctx, Stream body, CancellationToken ct = default)
    {
        var file = await DatasetUploads.StreamToTempAsync(_scratch, _fs, ctx.Ddb.DatasetFolderPath, body, ct);
        ctx.TrackTempFile(file.TempPath);
        return file;
    }

    public async Task<EntryDto> CommitAsync(UploadRequestContext ctx, string path, StreamedFile file)
    {
        var outcomes = await CommitPartsAsync(ctx, [new StreamedPart(path, file)]);
        var outcome = outcomes[0];
        if (outcome.Error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(outcome.Error);
        return outcome.Entry!;
    }

    public async Task<IReadOnlyList<UploadPartResultDto>> CommitAsync(UploadRequestContext ctx,
        IReadOnlyList<StreamedPart> parts)
    {
        var outcomes = await CommitPartsAsync(ctx, parts);
        return outcomes.Select(o => o.ToDto(authenticated: ctx.User != null)).ToArray();
    }

    public async Task FailAsync(UploadRequestContext ctx, Exception error)
    {
        if (ctx.UploadId == null || _receipts == null)
            return;

        try
        {
            // A client that went away sends the file again: forget the attempt
            if (error is OperationCanceledException or IOException or BadHttpRequestException)
            {
                await _receipts.RemoveAsync(ctx.OrgSlug, ctx.DsSlug, ctx.UploadId);
                return;
            }

            var d = ApiExceptionClassifier.Classify(error, authenticated: ctx.User != null);
            await _receipts.SetAsync(ctx.OrgSlug, ctx.DsSlug, ctx.UploadId,
                new UploadReceipt(UploadReceiptState.Failed, null, d.StatusCode, d.Message, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot record the failure of upload {UploadId}", ctx.UploadId);
        }
    }

    public async Task<UploadReceipt?> GetReceiptAsync(string orgSlug, string dsSlug, string uploadId)
    {
        if (!IsValidUploadId(uploadId))
            throw new BadRequestException("Invalid upload id");

        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write);

        return _receipts == null ? null : await _receipts.GetAsync(orgSlug, dsSlug, uploadId);
    }

    private sealed record PartOutcome(string Path, EntryDto? Entry, Exception? Error)
    {
        public UploadPartResultDto ToDto(bool authenticated)
        {
            if (Error == null)
                return new UploadPartResultDto { Path = Path, Status = StatusCodes.Status201Created, Entry = Entry };

            var d = ApiExceptionClassifier.Classify(Error, authenticated);
            return new UploadPartResultDto
            {
                Path = Path,
                Status = d.StatusCode,
                Error = d.Message,
                Retryable = !d.NoRetry && d.StatusCode is StatusCodes.Status503ServiceUnavailable
                    or StatusCodes.Status429TooManyRequests
            };
        }
    }

    // A file moved into the dataset and waiting for (or past) its index step
    private sealed class MovedPart(StreamedPart part, string localPath)
    {
        public StreamedPart Part { get; } = part;
        public string LocalPath { get; } = localPath;
        public Entry? Entry { get; set; }
        public Exception? Error { get; set; }
    }

    private async Task<IReadOnlyList<PartOutcome>> CommitPartsAsync(UploadRequestContext ctx,
        IReadOnlyList<StreamedPart> parts)
    {
        _logger.LogInformation("Committing {Count} uploaded file(s) to '{OrgSlug}/{DsSlug}'", parts.Count,
            ctx.OrgSlug, ctx.DsSlug);

        var outcomes = new PartOutcome?[parts.Count];
        var moved = new List<(int Index, MovedPart Part)>();
        try
        {
            await SetReceiptAsync(ctx, UploadReceiptState.Committing, null);

            // Validation first: a refused file is never moved into the dataset; a valid one is moved
            // to its normalized path
            var valid = new List<int>();
            var sizes = new long[parts.Count];
            var targets = new string[parts.Count];
            for (var i = 0; i < parts.Count; i++)
            {
                try
                {
                    ValidateTempFile(ctx, parts[i].File.TempPath);
                    targets[i] = ValidatePath(ctx, parts[i].Path);
                    sizes[i] = ActualSize(parts[i].File);
                    valid.Add(i);
                }
                catch (Exception ex)
                {
                    outcomes[i] = new PartOutcome(parts[i].Path, null, ex);
                }
            }

            // One quota decision for the request, on the authoritative sizes on disk
            var bytes = valid.Sum(i => sizes[i]);
            await ReserveAsync(ctx, bytes);

            foreach (var i in valid)
            {
                var part = parts[i] with { Path = targets[i] };
                var localFilePath = ctx.Ddb.GetLocalPath(part.Path);
                _fs.EnsureParentFolderExists(localFilePath);

                // Same-volume atomic move: no extra full-file copy (keeps the write time the hash hint carries)
                _fs.Move(part.File.TempPath, localFilePath, overwrite: true);
                moved.Add((i, new MovedPart(part, localFilePath)));
            }

            await IndexAsync(ctx, moved.Select(m => m.Part).ToArray());

            foreach (var (i, m) in moved)
            {
                if (m.Error != null)
                {
                    // Retryable index contention: the file on disk is healthy and was never committed
                    // to the index; reconciliation + client retry (503 + Retry-After) is enough.
                    // Anything else: quarantine instead of leaving an untracked file behind.
                    if (m.Error is not TransientException and not DdbBusyException)
                        Quarantine(ctx, m.Part.Path, m.LocalPath, m.Error);
                    outcomes[i] = new PartOutcome(m.Part.Path, null, m.Error);
                }
            }

            var indexed = moved.Where(m => m.Part.Entry != null).ToArray();
            if (indexed.Length > 0)
            {
                ctx.Reservation?.Resize(indexed.Sum(m => sizes[m.Index]));
                ctx.Reservation?.Commit();
            }

            // Post-index finalization: the entries are committed, failures here never quarantine
            await FinalizeAsync(ctx, indexed.Select(m => m.Part).ToArray());
            foreach (var (i, m) in indexed)
                outcomes[i] = new PartOutcome(m.Part.Path, m.Entry!.ToDto(), null);
        }
        catch (Exception ex)
        {
            // Request-level failure (quota, a native outage): every file not settled shares it
            for (var i = 0; i < parts.Count; i++)
                outcomes[i] ??= new PartOutcome(parts[i].Path, null, ex);
        }
        finally
        {
            // Whatever happened, this request enqueues nothing more: the index lane must not wait for it
            ctx.Ticket?.End();

            // A part that was never moved (validation or quota failure) leaves no temp file behind;
            // a path outside this dataset's uploads folder is never ours to delete
            foreach (var part in parts)
                if (!string.IsNullOrWhiteSpace(part.File.TempPath) && IsInUploadsFolder(ctx.Ddb, part.File.TempPath))
                    _fs.TryDelete(part.File.TempPath); // else the abandoned-upload sweep removes it
        }

        var results = outcomes.Select((o, i) => o ?? new PartOutcome(parts[i].Path, null,
            new InvalidOperationException("Upload part was not processed"))).ToArray();

        var dtos = results.Select(r => r.ToDto(authenticated: ctx.User != null)).ToArray();
        if (results.All(r => r.Error != null))
            await FailAsync(ctx, results[0].Error!);
        else
            await SetReceiptAsync(ctx, UploadReceiptState.Done, dtos);
        return results;
    }

    private long ActualSize(StreamedFile file)
    {
        // Authoritative size from disk: never trust the caller-provided count for the quota check
        var actual = _fs.GetFileSize(file.TempPath);
        if (actual != file.Bytes)
            _logger.LogWarning("Streamed upload size mismatch: {Reported} bytes reported, temp file is {Actual} bytes",
                file.Bytes, actual);
        return actual;
    }

    private async Task ReserveAsync(UploadRequestContext ctx, long bytes)
    {
        if (ctx.Reservation == null)
            ctx.Reservation = await _quota.ReserveAsync(QuotaUserId(ctx.User), bytes);
        else
            ctx.Reservation.Resize(bytes);
    }

    private static string QuotaUserId(Registry.Web.Identity.Models.User? user) => user?.Id ?? string.Empty;

    // Defense-in-depth: the temp file must live inside this dataset's reserved uploads folder, so an
    // arbitrary/foreign path is never moved nor deleted.
    private static void ValidateTempFile(UploadRequestContext ctx, string tempFilePath)
    {
        if (string.IsNullOrWhiteSpace(tempFilePath))
            throw new InvalidOperationException("Missing temporary upload path");

        if (!IsInUploadsFolder(ctx.Ddb, tempFilePath))
            throw new InvalidOperationException("Invalid temporary upload path");
    }

    internal static bool IsInUploadsFolder(IDDB ddb, string tempFilePath) =>
        CommonUtils.IsUnder(Path.Combine(ddb.DatasetFolderPath, IDDB.UploadsFolderName), tempFilePath);

    // One index batch for every file of the request; each file gets its own entry or error
    private async Task IndexAsync(UploadRequestContext ctx, IReadOnlyList<MovedPart> parts)
    {
        if (parts.Count == 0)
        {
            ctx.Ticket?.End();
            return;
        }

        _logger.LogInformation("Files saved, adding {Count} to DDB", parts.Count);

        if (_indexQueue != null)
        {
            // Coalesces with other concurrent uploads to the same dataset into one native batch and
            // one write transaction; the hashes computed while streaming save reading the files again.
            var outcomes = await _indexQueue.EnqueueAsync(ctx.Key,
                parts.Select(p => new AddItem(p.Part.Path, p.Part.File.Hash)).ToArray(), ctx.Ticket);
            for (var i = 0; i < parts.Count; i++)
            {
                parts[i].Entry = outcomes[i].Entry;
                parts[i].Error = outcomes[i].Error ??
                                 (outcomes[i].Entry == null ? new InvalidOperationException("Cannot find just added file!") : null);
            }

            return;
        }

        // Fallback for callers/tests that construct the pipeline without DI wiring
        foreach (var part in parts)
        {
            try
            {
                ctx.Ddb.AddRaw(part.Part.Path);
                part.Entry = ctx.Ddb.GetEntry(part.Part.Path) ??
                             throw new DdbException("Cannot find just added file!");
            }
            catch (Exception ex)
            {
                part.Error = ex;
            }
        }
    }

    /// <summary>
    /// Compensating action for the commit flow: moves a file that was placed on disk but failed
    /// to index into a per-dataset quarantine folder, so it is neither lost nor left as an
    /// untracked orphan. Never throws — a failed compensation is logged for the reconciliation
    /// sweep to pick up.
    /// </summary>
    private void Quarantine(UploadRequestContext ctx, string path, string localFilePath, Exception cause)
    {
        try
        {
            if (!_fs.Exists(localFilePath))
                return;

            var quarantineDir = Path.Combine(ctx.Ddb.DatasetFolderPath, IDDB.UploadsFolderName, IDDB.QuarantineFolderName);
            _fs.FolderCreate(quarantineDir);

            // GUID suffix for collision resistance: parallel failures for paths with the same
            // basename (e.g. a/data.tif, b/data.tif) could otherwise generate the same target
            // and overwrite silently.
            var quarantinePath = Path.Combine(quarantineDir,
                $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}-{Path.GetFileName(path)}");

            _fs.Move(localFilePath, quarantinePath, overwrite: true);

            _logger.LogError(cause,
                "Quarantined '{Path}' in dataset '{OrgSlug}/{DsSlug}' after index failure -> '{QuarantinePath}'",
                path, ctx.OrgSlug, ctx.DsSlug, quarantinePath);
        }
        catch (Exception quarantineEx)
        {
            _logger.LogError(quarantineEx,
                "Failed to quarantine '{Path}' in dataset '{OrgSlug}/{DsSlug}' after index failure (original cause: {Cause})",
                path, ctx.OrgSlug, ctx.DsSlug, cause.Message);
        }
    }

    /// <summary>
    /// After the files are indexed: schedule builds and thumbnails, then invalidate the caches
    /// that depend on the entry set once for the request.
    /// </summary>
    private async Task FinalizeAsync(UploadRequestContext ctx, IReadOnlyList<MovedPart> parts)
    {
        if (parts.Count == 0)
            return;

        var buildable = parts.Where(p => ctx.Ddb.IsBuildable(p.Entry!.Path)).ToArray();
        if (buildable.Length > 0)
        {
            // CRITICAL: Set pending flag BEFORE build starts
            // This prevents race condition where build fails quickly and creates .pending
            // but recurring job hasn't updated cache yet
            await _buildPendingService.SetDatasetHasPendingBuilds(ctx.OrgSlug, ctx.DsSlug, true);

            foreach (var part in buildable)
            {
                var entry = part.Entry!;
                var meta = new IndexPayload(ctx.OrgSlug, ctx.DsSlug, entry.Hash, ctx.User?.Id, null, entry.Path);
                var jobId = _backgroundJob.EnqueueBuild(ctx.Key, part.Part.Path, false, meta);
                _logger.LogInformation("Build of '{Path}' queued as job {JobId}", entry.Path, jobId);
            }
        }

        foreach (var part in parts.Except(buildable))
        {
            if (part.Entry!.Type is not (EntryType.Image or EntryType.GeoImage) || _thumbnails == null)
                continue;

            _thumbnails.TryEnqueue(new ThumbnailPrecomputeRequest(ctx.OrgSlug, ctx.DsSlug, part.Entry.Hash,
                part.LocalPath, _defaultThumbnailSize));
        }

        // OGC documents and the dataset thumbnail: once per dataset per window, not per file
        _changes.FilesAdded(ctx.OrgSlug, ctx.DsSlug, parts.Select(p => p.Part.Path));
    }

    private async Task SetReceiptAsync(UploadRequestContext ctx, UploadReceiptState state,
        IReadOnlyList<UploadPartResultDto>? results)
    {
        if (ctx.UploadId == null || _receipts == null)
            return;

        try
        {
            await _receipts.SetAsync(ctx.OrgSlug, ctx.DsSlug, ctx.UploadId,
                new UploadReceipt(state, results, null, null, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot record upload {UploadId} as {State}", ctx.UploadId, state);
        }
    }
}
