using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Import;

/// <summary>The file of a URL a task imports, and where it goes in the dataset.</summary>
/// <param name="Url">The http/https URL (parsed and checked).</param>
/// <param name="FileName">The sanitized file name.</param>
/// <param name="Target">The dataset path the file is written at.</param>
/// <param name="Overwrite">Whether the request replaces an existing file at <paramref name="Target"/>.</param>
/// <param name="Username">The basic-auth user, or null.</param>
/// <param name="Password">The basic-auth password in clear, or empty.</param>
public sealed record FileUrlDownload(Uri Url, string FileName, string Target, bool Overwrite, string? Username,
    string Password);

/// <summary>
/// The transfer of a file of a URL into a dataset, for the import-file task: the file is streamed through the
/// SSRF-hardened <see cref="GuardedHttpDownloader"/> (size cap, disk head-room, low-speed guard, transfer timeout) to a
/// scratch file on the dataset's volume, then recorded in the task's write record (FR-XC-27) and moved into place,
/// never over a file that appeared meanwhile.
/// </summary>
public sealed class FileUrlTransfer
{
    private readonly GuardedHttpDownloader _downloader;
    private readonly ScratchFileProvider _scratch;
    private readonly ImportSettings _importSettings;
    private readonly long _diskSafetyMarginBytes;
    private readonly IFileSystem _fs;

    /// <param name="downloader">The SSRF-hardened, budget-guarded downloader.</param>
    /// <param name="scratch">Where the download is written: the dataset's volume, so the move into place is a rename.</param>
    /// <param name="importSettings">The import settings (size cap, speed guard, transfer timeout).</param>
    /// <param name="platformSettings">The processing platform settings (disk safety margin).</param>
    /// <param name="fs">The file system the dataset folder is written through.</param>
    public FileUrlTransfer(GuardedHttpDownloader downloader, ScratchFileProvider scratch,
        IOptions<ImportSettings> importSettings, IOptions<ProcessingPlatformSettings> platformSettings, IFileSystem fs)
    {
        _downloader = downloader;
        _scratch = scratch;
        _importSettings = importSettings.Value;
        _diskSafetyMarginBytes = platformSettings.Value.DiskSafetyMarginBytes;
        _fs = fs;
    }

    /// <summary>Refuses a host the downloader would refuse (the fast check at submit; the connection checks again).</summary>
    public Task AssertHostAllowedAsync(string host, CancellationToken ct) => _downloader.AssertHostAllowedAsync(host, ct);

    /// <summary>
    /// Downloads <paramref name="download"/> to a scratch path on the dataset volume, then moves it to its target (a
    /// rename) and marks it written: from the move on, a later attempt of the task only indexes it.
    /// </summary>
    /// <exception cref="NameClashException">A file is at the target and the request does not replace it.</exception>
    public async Task DownloadIntoPlaceAsync(FileUrlDownload download, string orgSlug, IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(ctx);
        var (uri, fileName, target, overwrite, username, password) = download;
        var cap = _importSettings.EffectiveFileImportCapBytes();

        // The smaller of the size limit and what the owner of the organization may still store, read now, when the file is
        // about to be written (FR-XC-26, D-039): a file that does not fit fails the task with the 507 text, and nothing of
        // it stays (the download is a scratch file until it is moved into place)
        string? capMessage = null;
        if (await ctx.Storage.RemainingBytesOfOrganizationAsync(orgSlug, ct) is { } left)
        {
            if (left <= 0)
                throw QuotaExceededException.OwnerOverQuota(left);
            if (cap <= 0 || left < cap)
            {
                cap = left;
                capMessage = QuotaExceededException.OwnerOverQuotaMessage(left);
            }
        }

        // The download goes to the dataset's uploads folder: never indexed, and on the dataset's volume,
        // so the disk guard measures the volume it fills and the move into place is a rename, not a
        // full copy from /tmp. Deleted on any failure (Dispose), kept once moved.
        using var scratch = _scratch.CreateForDataset(ctx.Ddb.DatasetFolderPath);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var transferTimeout = _importSettings.TransferTimeoutSeconds;
        if (transferTimeout > 0)
            linkedCts.CancelAfter(TimeSpan.FromSeconds(transferTimeout));

        progress.Report(new HeavyToolProgress(-1, "downloading",
            LogChunk: $"Downloading {fileName} from {uri.Host}"));

        var sink = new CallbackProgress<FileDownloadProgress>(p =>
            progress.Report(new HeavyToolProgress(p.Fraction, "downloading",
                p.TotalBytes is > 0
                    ? $"Downloaded {CommonUtils.GetBytesReadable(p.BytesSoFar)} / {CommonUtils.GetBytesReadable(p.TotalBytes.Value)}"
                    : $"Downloaded {CommonUtils.GetBytesReadable(p.BytesSoFar)}")));

        long downloaded;
        try
        {
            downloaded = await _downloader.DownloadAsync(uri, scratch.Path, username, password,
                cap, _importSettings.MinDownloadSpeedBytesPerSec, _importSettings.LowSpeedGraceSeconds,
                _diskSafetyMarginBytes, sink, linkedCts.Token, capMessage);
        }
        catch (OperationCanceledException)
            when (transferTimeout > 0 && linkedCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The download exceeded the configured transfer timeout of {transferTimeout}s.");
        }

        var localTarget = ctx.Ddb.GetLocalPath(target);
        var root = ctx.Ddb.DatasetFolderPath;

        progress.Report(new HeavyToolProgress(-1, "indexing",
            LogChunk: $"Adding {fileName} ({CommonUtils.GetBytesReadable(downloaded)}) to the dataset"));

        // What the task creates is recorded before it is created (FR-XC-27): the folders and the file, which is complete
        // and stamped here, in its scratch file. Decided before the move: a file the request replaces (overwrite,
        // FR-XC-07) is never recorded, a rollback must not delete the file that was there.
        var writes = await TaskImportWrites.StartAsync(ctx.WriteRecord, ctx.Dataset, root, _fs, ct);
        var replacing = overwrite && _fs.Exists(localTarget);
        if (!overwrite && _fs.Exists(localTarget))
            throw new NameClashException(target);
        // Saved before the folders and the file exist (§8.1): a process lost after the move leaves a record that rolls
        // it back
        TaskImportWrites.RecordFolders(_fs, writes, root, TaskImportWrites.ParentOf(target));
        if (!replacing)
        {
            writes.Record(target);
            writes.Written(target, scratch.Path);
        }

        await writes.SaveAsync(ct);
        TaskImportWrites.CreateFolder(_fs, root, TaskImportWrites.ParentOf(target));

        // Overwrite was already checked (ResolveOutput). A new file never replaces one that appeared meanwhile; a
        // replaced one is moved over (a rename on the same volume, atomic over an existing file)
        try
        {
            scratch.MoveTo(localTarget, overwrite: replacing);
        }
        catch (IOException) when (!replacing && _fs.Exists(localTarget))
        {
            await writes.ForgetAsync(target, CancellationToken.None);
            throw new NameClashException(target);
        }

        // The very next step after the move: from here a later attempt indexes the file. NOTE: a process crash
        // between the move and this line leaves the file without the marker (the retry then refuses it as existing)
        ctx.MarkOutputPublished(target);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
