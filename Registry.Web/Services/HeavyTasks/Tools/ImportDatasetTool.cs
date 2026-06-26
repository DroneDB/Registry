#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that populates a freshly created (empty) dataset from a remote source - another Registry
/// instance or a downloadable archive (spec ImportDataset). Runs on the Hangfire worker, HTTP-context
/// free, and works entirely through <see cref="IDDB"/>: the resolved <see cref="IImportSource"/> writes
/// files straight into the dataset folder, then the tool indexes them with <c>AddRawBatch</c> and
/// enqueues a per-file build job for every buildable entry (mirrors <see cref="ArchiveExtractTool"/>).
/// Mutates the dataset in place, so it produces no downloadable artifact.
/// </summary>
public sealed class ImportDatasetTool : IHeavyTool
{
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

    private readonly IImportSourceFactory _factory;
    private readonly IImportCredentialProtector _protector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ImportSettings _settings;
    private readonly ILogger<ImportDatasetTool> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportDatasetTool"/> class.
    /// </summary>
    /// <param name="factory">The import source factory.</param>
    /// <param name="protector">The credential protector used to decrypt stored secrets.</param>
    /// <param name="scopeFactory">Scope factory for per-execution scoped services.</param>
    /// <param name="appSettings">The application settings.</param>
    /// <param name="logger">The logger.</param>
    public ImportDatasetTool(
        IImportSourceFactory factory,
        IImportCredentialProtector protector,
        IServiceScopeFactory scopeFactory,
        IOptions<AppSettings> appSettings,
        ILogger<ImportDatasetTool> logger)
    {
        _factory = factory;
        _protector = protector;
        _scopeFactory = scopeFactory;
        _settings = appSettings.Value.Import ?? new ImportSettings();
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "import-dataset";

    /// <inheritdoc />
    public string Version => "1";

    /// <inheritdoc />
    public string Title => "Import dataset";

    /// <inheritdoc />
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;

    /// <inheritdoc />
    public bool ProducesArtifact => false;

    /// <inheritdoc />
    public JsonDocument InputSchema => Schema;

    // One DDB transaction (and one native connection open) per chunk instead of one-per-file.
    private const int IndexChunkSize = 250;

    // Headroom kept free on the dataset volume while fetching; the streaming guard aborts before
    // the projected size would eat into it.
    private const long DiskSafetyMarginBytes = 256L * 1024 * 1024;

    /// <inheritdoc />
    public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var sourceType = ReadString(request.Params, "sourceType");
        if (string.IsNullOrWhiteSpace(sourceType))
            throw new ArgumentException("A source type is required.");

        if (!_factory.AvailableTypes.Contains(sourceType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown or disabled import source type '{sourceType}'.");

        if (request.Params.ValueKind != JsonValueKind.Object ||
            !request.Params.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Source parameters are required.");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
        => new(ReadLong(request.Params, "budgetBytes"), QuotaKey: "import-dataset",
            DefaultFileName: null, ContentType: null);

    /// <inheritdoc />
    public async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        var sourceType = ReadString(request.Params, "sourceType")
                         ?? throw new InvalidOperationException("A source type is required.");
        var budgetBytes = ReadLong(request.Params, "budgetBytes");

        if (!request.Params.TryGetProperty("params", out var rawParams) ||
            rawParams.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Source parameters are required.");

        var sourceParams = DecryptParams(rawParams, _protector);
        var source = _factory.Resolve(sourceType);

        var root = ctx.Ddb.DatasetFolderPath;
        Directory.CreateDirectory(root);

        // Resumability: files already present from a previous partial run are kept; only the files
        // written during THIS run are rolled back on a budget breach.
        var before = EnumerateRelativeFiles(root).ToHashSet(StringComparer.Ordinal);

        // Effective cap = the tightest of: per-user remaining quota (budgetBytes), the absolute
        // MaxImportSizeBytes ceiling, and the free disk space (minus a safety margin).
        var cap = long.MaxValue;
        if (_settings.MaxImportSizeBytes > 0) cap = Math.Min(cap, _settings.MaxImportSizeBytes);
        if (budgetBytes is >= 0) cap = Math.Min(cap, budgetBytes.Value);
        var freeAtStart = GetAvailableDiskBytes(root);

        long bytesSoFar = 0;
        var breached = false;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        progress.Report(new HeavyToolProgress(-1, "fetching",
            LogChunk: $"Importing from '{sourceType}'"));

        // Synchronous sink so a budget breach cancels the transfer promptly (Progress<T> would post
        // the callback asynchronously and keep downloading in the meantime).
        var sink = new CallbackProgress<ImportProgress>(p =>
        {
            if (p.BytesSoFar is { } b)
            {
                bytesSoFar = b;
                if ((cap != long.MaxValue && b > cap) || freeAtStart - b < DiskSafetyMarginBytes)
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

        if (breached)
        {
            var added = EnumerateRelativeFiles(root).Where(f => !before.Contains(f)).ToList();
            progress.Report(new HeavyToolProgress(-1, "cleanup",
                LogChunk: $"Storage budget exceeded - rolling back {added.Count} file(s)"));
            CleanupFiles(root, added);
            throw new QuotaExceededException(
                "The import exceeded the available storage budget. " +
                $"Rolled back after writing {CommonUtils.GetBytesReadable(bytesSoFar)}.");
        }

        // A genuine user cancellation (not a breach) leaves partial files in place for a later resume.
        ct.ThrowIfCancellationRequested();

        // Index everything currently in the dataset folder, in batches. The returned entries carry
        // Type + Hash, so buildable files can be scheduled without extra per-file native calls.
        var allFiles = EnumerateRelativeFiles(root).ToList();
        var indexed = new List<Entry>(allFiles.Count);
        for (var i = 0; i < allFiles.Count; i += IndexChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = allFiles.GetRange(i, Math.Min(IndexChunkSize, allFiles.Count - i));
            indexed.AddRange(ctx.Ddb.AddRawBatch(slice));
            progress.Report(new HeavyToolProgress(
                allFiles.Count > 0 ? (double)(i + slice.Count) / allFiles.Count : -1,
                "indexing", LogChunk: $"Indexed {i + slice.Count}/{allFiles.Count} file(s)"));
        }

        // Enqueue a per-file build job for every buildable entry (mirrors ObjectsManager.AddNew).
        using (var scope = _scopeFactory.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobsProcessor>();
            var buildableCount = 0;

            foreach (var entry in indexed)
            {
                if (!ctx.Ddb.IsBuildable(entry.Path))
                    continue;

                var path = entry.Path;
                var meta = new IndexPayload(
                    request.OrgSlug,
                    request.DsSlug,
                    entry.Hash,
                    null,
                    Path: path,
                    ParentJobId: ctx.TaskId);

                Expression<Action> buildJob = () => HangfireUtils.BuildWrapper(ctx.Ddb, path, false, null);
                processor.EnqueueIndexed(buildJob, meta);
                buildableCount++;
            }

            _logger.LogInformation("Enqueued {Count} build job(s) for imported files in {Org}/{Ds}",
                buildableCount, request.OrgSlug, request.DsSlug);
        }

        // Invalidate cached tiles/thumbnails/OGC (no auth needed; keyed by org/ds).
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var cacheInvalidator = scope.ServiceProvider.GetRequiredService<IDatasetCacheInvalidator>();
            await cacheInvalidator.InvalidateAllDatasetCachesAsync(request.OrgSlug, request.DsSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache invalidation after import failed for {Org}/{Ds}",
                request.OrgSlug, request.DsSlug);
        }

        progress.Report(new HeavyToolProgress(1, "done",
            LogChunk: $"Import complete: {allFiles.Count} file(s) indexed"));
        return null;
    }

    // Walks the dataset folder, returning forward-slash relative paths and excluding the reserved
    // .ddb index folder.
    private static IEnumerable<string> EnumerateRelativeFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;

        var reserved = IDDB.DatabaseFolderName + "/";
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith(reserved, StringComparison.Ordinal)) continue;
            yield return rel;
        }
    }

    private void CleanupFiles(string root, IEnumerable<string> relativePaths)
    {
        foreach (var rel in relativePaths)
        {
            try
            {
                var local = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(local))
                    File.Delete(local);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete partially imported file '{Path}'", rel);
            }
        }
    }

    // Returns a copy of the source params with any "ENC:"-prefixed string value decrypted in place.
    private static JsonElement DecryptParams(JsonElement paramsEl, IImportCredentialProtector protector)
    {
        var obj = new JsonObject();
        foreach (var prop in paramsEl.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString() ?? string.Empty;
                obj[prop.Name] = s.StartsWith("ENC:", StringComparison.Ordinal)
                    ? protector.Unprotect(s[4..])
                    : s;
            }
            else
            {
                obj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
        }

        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static long GetAvailableDiskBytes(string datasetFolderPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(datasetFolderPath));
            if (string.IsNullOrEmpty(root)) return long.MaxValue;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : long.MaxValue;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static long? ReadLong(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) ? v : null;
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
