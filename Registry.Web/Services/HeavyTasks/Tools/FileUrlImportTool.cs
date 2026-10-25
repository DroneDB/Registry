#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Import;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that downloads a single file from an http/https URL into an existing dataset
/// (<see cref="Id"/> = <c>import-file</c>). Runs on the Hangfire worker, HTTP-context free. The file is
/// streamed to a scratch path OUTSIDE the dataset (never indexed) through the SSRF-hardened
/// <see cref="GuardedHttpDownloader"/> - which enforces the size cap, disk head-room and the low-speed
/// guard - then moved into the dataset, indexed with <c>AddRaw</c> and (when buildable) a build job is
/// enqueued (mirrors <see cref="ImportDatasetTool"/>). Mutates the dataset in place, so it produces no
/// downloadable artifact.
/// </summary>
public sealed class FileUrlImportTool : HeavyToolBase<FileUrlImportTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string? Url, string? FileName, string? Folder, bool? Overwrite, long? SizeBytes, string? Username, string? Password);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.Text("url"),
        p.Text("fileName"),
        p.Text("folder"),
        p.Flag("overwrite"),
        p.WholeNumber("sizeBytes"),
        p.Text("username"),
        p.Text("password"));

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["url", "fileName"],
          "properties": {
            "url":       { "type": "string", "title": "File URL" },
            "fileName":  { "type": "string", "title": "File name" },
            "folder":    { "type": ["string", "null"], "title": "Destination folder" },
            "overwrite": { "type": "boolean", "title": "Overwrite existing file" },
            "username":  { "type": ["string", "null"], "title": "Basic-auth user" },
            "password":  { "type": ["string", "null"], "title": "Basic-auth password (encrypted)" },
            "sizeBytes": { "type": ["integer", "null"], "title": "Advertised size (bytes)" }
          },
          "additionalProperties": false
        }
        """);

    private readonly GuardedHttpDownloader _downloader;
    private readonly ImportCredentials _credentials;
    private readonly TaskFileIndexer _indexer;
    private readonly ImportSettings _importSettings;
    private readonly long _diskSafetyMarginBytes;
    private readonly DatasetPathPolicy _paths;
    private readonly ScratchFileProvider _scratch;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileUrlImportTool"/> class.
    /// </summary>
    /// <param name="downloader">The SSRF-hardened, budget-guarded downloader (it also pre-validates the host at submit time).</param>
    /// <param name="credentials">Decrypts the basic-auth password.</param>
    /// <param name="indexer">Indexes the imported file, queues its build and invalidates the dataset's caches.</param>
    /// <param name="importSettings">The import settings.</param>
    /// <param name="platformSettings">The processing platform settings (size cap, disk safety margin).</param>
    /// <param name="paths">The dataset path rule (destination folder and file).</param>
    /// <param name="scratch">Where the download is written: the dataset's volume, so the move into place is a rename.</param>
    public FileUrlImportTool(
        GuardedHttpDownloader downloader,
        ImportCredentials credentials,
        TaskFileIndexer indexer,
        IOptions<ImportSettings> importSettings, IOptions<ProcessingPlatformSettings> platformSettings,
        DatasetPathPolicy paths,
        ScratchFileProvider scratch)
    {
        _scratch = scratch;
        _downloader = downloader;
        _paths = paths;
        _credentials = credentials;
        _indexer = indexer;
        _importSettings = importSettings.Value;
        _diskSafetyMarginBytes =
            platformSettings.Value.DiskSafetyMarginBytes;
    }

    /// <inheritdoc />
    public override string Id => "import-file";

    /// <inheritdoc />
    public override string Version => "1";

    /// <inheritdoc />
    public override string Title => "Import file from URL";

    /// <inheritdoc />
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;

    /// <inheritdoc />
    public override bool ProducesArtifact => false;

    /// <inheritdoc />
    public override JsonDocument InputSchema => Schema;

    /// <inheritdoc />
    protected override async Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var uri = FileImportPolicy.ParseHttpUrl(args.Url);

        // Fast SSRF rejection at submit time (re-validated at connect time during download).
        await _downloader.AssertHostAllowedAsync(uri.Host, ct);

        var fileName = FileImportPolicy.SanitizeFileName(args.FileName);
        if (!_importSettings.IsExtensionAllowed(fileName))
            throw new ArgumentException($"Files of type '{Path.GetExtension(fileName)}' are not allowed.");

        var target = TargetPath(args.Folder, fileName);

        var overwrite = args.Overwrite == true;
        if (!overwrite && ctx.Ddb.EntryExists(target))
            throw new ArgumentException(
                $"'{target}' already exists. Enable overwrite to replace it.");
    }

    /// <inheritdoc />
    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
        => new(args.SizeBytes, QuotaKey: "import-file",
            DefaultFileName: null, ContentType: null);

    /// <inheritdoc />
    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        var uri = FileImportPolicy.ParseHttpUrl(args.Url);
        var fileName = FileImportPolicy.SanitizeFileName(args.FileName);

        // Defense in depth: re-check the extension policy on the worker so a job enqueued before the
        // allow/block-list was tightened (or a crafted job) cannot import a now-disallowed type.
        if (!_importSettings.IsExtensionAllowed(fileName))
            throw new InvalidOperationException($"Files of type '{Path.GetExtension(fileName)}' are not allowed.");

        var target = TargetPath(args.Folder, fileName);

        var overwrite = args.Overwrite == true;
        if (!overwrite && ctx.Ddb.EntryExists(target))
            throw new InvalidOperationException($"'{target}' already exists.");

        var username = args.Username;
        var password = DecryptPassword(args.Password);
        var cap = _importSettings.EffectiveFileImportCapBytes();

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
                _diskSafetyMarginBytes, sink, linkedCts.Token);
        }
        catch (OperationCanceledException)
            when (transferTimeout > 0 && linkedCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The download exceeded the configured transfer timeout of {transferTimeout}s.");
        }

        var localTarget = ctx.Ddb.GetLocalPath(target);
        var parent = Path.GetDirectoryName(localTarget);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        progress.Report(new HeavyToolProgress(-1, "indexing",
            LogChunk: $"Adding {fileName} ({CommonUtils.GetBytesReadable(downloaded)}) to the dataset"));

        // Overwrite was already validated above; move the scratch file into place (a rename on the
        // same volume, atomic over an existing file)
        File.Move(scratch.Path, localTarget, overwrite: true);
        scratch.Release();

        // Index it, queue its build when libddb reports it buildable (a child of the task), invalidate the
        // dataset's caches: what every task that wrote files does (TaskFileIndexer)
        await _indexer.IndexAsync(new TaskFiles(ctx.Ddb, ctx.Dataset, request.OrgSlug, request.DsSlug, [target],
            ctx.BuildQueue, ctx.TaskId, "imported file"), progress, ct);

        progress.Report(new HeavyToolProgress(1, "done", LogChunk: $"Imported {target}"));
        return null;
    }

    // The dataset path of the (already sanitized) file name in the optional folder: libddb's path
    // rule on the whole path, so a reserved result (the root ".uploads", any ".ddb") is refused
    private string TargetPath(string? folder, string fileName)
    {
        var f = _paths.NormalizeFolder(folder);
        return _paths.Normalize(f.Length == 0 ? fileName : $"{f}/{fileName}", "destination path");
    }

    private string DecryptPassword(string? raw) =>
        string.IsNullOrEmpty(raw) ? string.Empty : _credentials.Open(raw);

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
