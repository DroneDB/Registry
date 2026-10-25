#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Import;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that populates a freshly created (empty) dataset from a remote source - another Registry
/// instance or a downloadable archive (spec ImportDataset). Runs on the Hangfire worker, HTTP-context
/// free, and works entirely through <see cref="IDDB"/>: the resolved <see cref="IImportSource"/> writes
/// files straight into the dataset folder, then the tool indexes them with <c>AddRawBatch</c> and
/// enqueues a per-file build job for every buildable entry (mirrors <see cref="ArchiveExtractTool"/>).
/// Mutates the dataset in place, so it produces no downloadable artifact.
/// </summary>
public sealed class ImportDatasetTool : HeavyToolBase<ImportDatasetTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string? SourceType, long? BudgetBytes, JsonElement? SourceParams);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.Text("sourceType"),
        p.WholeNumber("budgetBytes"),
        p.JsonObject("params"));

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["sourceType", "params"],
          "properties": {
            "sourceType":  { "type": "string", "enum": ["registry", "archive-url"], "title": "Source" },
            "budgetBytes": { "type": ["integer", "null"], "title": "Storage budget (bytes)" },
            "params":      { "type": "object", "title": "Source parameters" }
          },
          "additionalProperties": false
        }
        """);

    private readonly ImportSourceCatalog _sources;
    private readonly ImportCredentials _credentials;
    private readonly TaskFileIndexer _indexer;
    private readonly ImportSettings _settings;
    private readonly long _diskSafetyMarginBytes;
    private readonly VolumeHeadroom _headroom;
    private readonly DatasetFolderFiles _files;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportDatasetTool"/> class.
    /// </summary>
    /// <param name="sources">The import sources the server offers (registered, narrowed by the allow-list).</param>
    /// <param name="credentials">Decrypts the stored secrets of the source parameters.</param>
    /// <param name="indexer">Indexes the imported files and queues their builds.</param>
    /// <param name="importSettings">The import settings.</param>
    /// <param name="platformSettings">The processing platform settings (size cap, disk safety margin).</param>
    /// <param name="headroom">The disk head-room of the dataset volume.</param>
    /// <param name="files">The files of the dataset folder: what was there, what the import added, the rollback.</param>
    public ImportDatasetTool(
        ImportSourceCatalog sources,
        ImportCredentials credentials,
        TaskFileIndexer indexer,
        IOptions<ImportSettings> importSettings, IOptions<ProcessingPlatformSettings> platformSettings,
        VolumeHeadroom headroom,
        DatasetFolderFiles files)
    {
        _headroom = headroom;
        _sources = sources;
        _credentials = credentials;
        _indexer = indexer;
        _settings = importSettings.Value;
        _diskSafetyMarginBytes =
            platformSettings.Value.DiskSafetyMarginBytes;
        _files = files;
    }

    /// <inheritdoc />
    public override string Id => "import-dataset";

    /// <inheritdoc />
    public override string Version => "1";

    /// <inheritdoc />
    public override string Title => "Import dataset";

    /// <inheritdoc />
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;

    /// <inheritdoc />
    public override bool ProducesArtifact => false;

    /// <inheritdoc />
    public override JsonDocument InputSchema => Schema;

    /// <inheritdoc />
    protected override Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        if (_sources.Refusal(args.SourceType) is { } refusal)
            throw new ArgumentException(refusal);

        if (args.SourceParams is null)
            throw new ArgumentException("Source parameters are required.");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
        => new(args.BudgetBytes, QuotaKey: "import-dataset",
            DefaultFileName: null, ContentType: null);

    /// <inheritdoc />
    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        var sourceType = args.SourceType
                         ?? throw new InvalidOperationException("A source type is required.");

        // Defense in depth: re-check the allow-list on the worker, so a job enqueued before the
        // allow-list was tightened (or a crafted job) cannot run a now-disabled source type.
        if (!_sources.IsAllowed(sourceType))
            throw new InvalidOperationException(ImportSourceCatalog.DisabledMessage(sourceType));

        var budgetBytes = args.BudgetBytes;

        var rawParams = args.SourceParams ?? throw new InvalidOperationException("Source parameters are required.");

        var sourceParams = _credentials.OpenParameters(sourceType, rawParams);
        var source = _sources.Resolve(sourceType);

        var root = ctx.Ddb.DatasetFolderPath;

        // Resumability: files already present from a previous partial run are kept; only the files
        // written during THIS run are rolled back on a budget breach.
        var before = _files.Prepare(root);

        // Effective cap = the tightest of: per-user remaining quota (budgetBytes), the absolute
        // MaxImportSizeBytes ceiling, and the free disk space minus the head-room (the safety
        // margin plus libddb's free-disk floor, VolumeHeadroom)
        var cap = long.MaxValue;
        if (_settings.MaxImportSizeBytes > 0) cap = Math.Min(cap, _settings.MaxImportSizeBytes);
        if (budgetBytes is >= 0) cap = Math.Min(cap, budgetBytes.Value);
        var freeAtStart = _headroom.AvailableBytes(root);
        var headroom = _headroom.MarginBytes(_diskSafetyMarginBytes);

        long bytesSoFar = 0;
        var breached = false;
        var timedOut = false;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Enforce the per-task transfer timeout (ImportSettings.TransferTimeoutSeconds). The named
        // HttpClient keeps a 24h backstop; this bounds the entire import deterministically.
        var transferTimeout = _settings.TransferTimeoutSeconds;
        if (transferTimeout > 0)
            linkedCts.CancelAfter(TimeSpan.FromSeconds(transferTimeout));

        progress.Report(new HeavyToolProgress(-1, "fetching",
            LogChunk: $"Importing from '{sourceType}'"));

        // Synchronous sink so a budget breach cancels the transfer promptly (Progress<T> would post
        // the callback asynchronously and keep downloading in the meantime).
        var sink = new CallbackProgress<ImportProgress>(p =>
        {
            if (p.BytesSoFar is { } b)
            {
                bytesSoFar = b;
                if ((cap != long.MaxValue && b > cap) || freeAtStart - b < headroom)
                {
                    breached = true;
                    // ReSharper disable once AccessToDisposedClosure
                    linkedCts.Cancel();
                    return;
                }
            }

            progress.Report(new HeavyToolProgress(p.Fraction, p.Phase ?? "fetching", p.Message));
        });

        try
        {
            await source.FetchAsync(sourceParams, root, sink, linkedCts.Token);
        }
        catch (OperationCanceledException) when (breached)
        {
            // Expected: the budget guard cancelled the transfer. Roll back below.
        }
        catch (OperationCanceledException)
            when (transferTimeout > 0 && linkedCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The per-task transfer timeout elapsed (not a user cancellation). Roll back below.
            timedOut = true;
        }

        if (breached || timedOut)
        {
            var added = _files.AddedSince(root, before);
            var reason = breached ? "Storage budget exceeded" : "Transfer timed out";
            progress.Report(new HeavyToolProgress(-1, "cleanup",
                LogChunk: $"{reason} - rolling back {added.Count} file(s)"));
            _files.Delete(root, added);

            if (breached)
                throw new QuotaExceededException(
                    "The import exceeded the available storage budget. " +
                    $"Rolled back after writing {CommonUtils.GetBytesReadable(bytesSoFar)}.");

            throw new TimeoutException(
                $"The import exceeded the configured transfer timeout of {transferTimeout}s.");
        }

        // A genuine user cancellation (not a breach/timeout) leaves partial files in place for resume.
        ct.ThrowIfCancellationRequested();

        // Index everything now in the dataset folder, queue the builds of the buildable files (children
        // of this task), invalidate the caches
        var allFiles = _files.All(root).ToList();
        await _indexer.IndexAsync(new TaskFiles(ctx.Ddb, ctx.Dataset, request.OrgSlug, request.DsSlug, allFiles,
            ctx.BuildQueue, ctx.TaskId, "imported files"), progress, ct);

        progress.Report(new HeavyToolProgress(1, "done",
            LogChunk: $"Import complete: {allFiles.Count} file(s) indexed"));
        return null;
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
