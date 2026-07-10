#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;
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
public sealed class FileUrlImportTool : IHeavyTool
{
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
    private readonly SsrfGuard _ssrfGuard;
    private readonly IImportCredentialProtector _protector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ImportSettings _importSettings;
    private readonly long _diskSafetyMarginBytes;
    private readonly ILogger<FileUrlImportTool> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileUrlImportTool"/> class.
    /// </summary>
    /// <param name="downloader">The SSRF-hardened, budget-guarded downloader.</param>
    /// <param name="ssrfGuard">The SSRF guard (fast pre-validation at submit time).</param>
    /// <param name="protector">The credential protector used to decrypt the basic-auth password.</param>
    /// <param name="scopeFactory">Scope factory for per-execution scoped services.</param>
    /// <param name="appSettings">The application settings.</param>
    /// <param name="logger">The logger.</param>
    public FileUrlImportTool(
        GuardedHttpDownloader downloader,
        SsrfGuard ssrfGuard,
        IImportCredentialProtector protector,
        IServiceScopeFactory scopeFactory,
        IOptions<AppSettings> appSettings,
        ILogger<FileUrlImportTool> logger)
    {
        _downloader = downloader;
        _ssrfGuard = ssrfGuard;
        _protector = protector;
        _scopeFactory = scopeFactory;
        _importSettings = appSettings.Value.Import ?? new ImportSettings();
        _diskSafetyMarginBytes =
            (appSettings.Value.ProcessingPlatform ?? new ProcessingPlatformSettings()).DiskSafetyMarginBytes;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "import-file";

    /// <inheritdoc />
    public string Version => "1";

    /// <inheritdoc />
    public string Title => "Import file from URL";

    /// <inheritdoc />
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;

    /// <inheritdoc />
    public bool ProducesArtifact => false;

    /// <inheritdoc />
    public JsonDocument InputSchema => Schema;

    /// <inheritdoc />
    public async Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var uri = FileImportPolicy.ParseHttpUrl(ReadString(request.Params, "url"));

        // Fast SSRF rejection at submit time (re-validated at connect time during download).
        await _ssrfGuard.AssertAllowedAsync(uri.Host, ct);

        var fileName = FileImportPolicy.SanitizeFileName(ReadString(request.Params, "fileName"));
        if (!_importSettings.IsExtensionAllowed(fileName))
            throw new ArgumentException($"Files of type '{Path.GetExtension(fileName)}' are not allowed.");

        var target = BuildTargetPath(ReadString(request.Params, "folder"), fileName);
        ValidateTarget(target);

        var overwrite = ReadBool(request.Params, "overwrite");
        if (!overwrite && ctx.Ddb.EntryExists(target))
            throw new ArgumentException(
                $"'{target}' already exists. Enable overwrite to replace it.");
    }

    /// <inheritdoc />
    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
        => new(ReadLong(request.Params, "sizeBytes"), QuotaKey: "import-file",
            DefaultFileName: null, ContentType: null);

    /// <inheritdoc />
    public async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        var uri = FileImportPolicy.ParseHttpUrl(ReadString(request.Params, "url"));
        var fileName = FileImportPolicy.SanitizeFileName(ReadString(request.Params, "fileName"));

        // Defense in depth: re-check the extension policy on the worker so a job enqueued before the
        // allow/block-list was tightened (or a crafted job) cannot import a now-disallowed type.
        if (!_importSettings.IsExtensionAllowed(fileName))
            throw new InvalidOperationException($"Files of type '{Path.GetExtension(fileName)}' are not allowed.");

        var target = BuildTargetPath(ReadString(request.Params, "folder"), fileName);
        ValidateTarget(target);

        var overwrite = ReadBool(request.Params, "overwrite");
        if (!overwrite && ctx.Ddb.EntryExists(target))
            throw new InvalidOperationException($"'{target}' already exists.");

        var username = ReadString(request.Params, "username");
        var password = DecryptPassword(ReadString(request.Params, "password"));
        var cap = _importSettings.EffectiveFileImportCapBytes();

        // Scratch file lives OUTSIDE the dataset folder so it is never indexed.
        var scratch = Path.Combine(Path.GetTempPath(), $"ddb-fileimport-{Guid.NewGuid():N}.part");

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
            downloaded = await _downloader.DownloadAsync(uri, scratch, username, password,
                cap, _importSettings.MinDownloadSpeedBytesPerSec, _importSettings.LowSpeedGraceSeconds,
                _diskSafetyMarginBytes, sink, linkedCts.Token);
        }
        catch (OperationCanceledException)
            when (transferTimeout > 0 && linkedCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryDelete(scratch);
            throw new TimeoutException(
                $"The download exceeded the configured transfer timeout of {transferTimeout}s.");
        }
        catch
        {
            TryDelete(scratch);
            throw;
        }

        try
        {
            var localTarget = ctx.Ddb.GetLocalPath(target);
            var parent = Path.GetDirectoryName(localTarget);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            progress.Report(new HeavyToolProgress(-1, "indexing",
                LogChunk: $"Adding {fileName} ({CommonUtils.GetBytesReadable(downloaded)}) to the dataset"));

            // Overwrite was already validated above; move the scratch file into place.
            File.Move(scratch, localTarget, overwrite: true);
            scratch = string.Empty; // moved: nothing to clean up

            ctx.Ddb.AddRaw(target);
        }
        catch
        {
            TryDelete(scratch);
            throw;
        }

        // Enqueue a build job when the imported file is buildable (mirrors ObjectsManager.AddNew).
        try
        {
            if (ctx.Ddb.IsBuildable(target))
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobsProcessor>();
                var entry = ctx.Ddb.GetEntry(target);
                var meta = new IndexPayload(
                    request.OrgSlug, request.DsSlug, entry?.Hash, null,
                    Path: target, ParentJobId: ctx.TaskId);

                Expression<Action> buildJob = () => HangfireUtils.BuildWrapper(ctx.Ddb, target, false, null);
                processor.EnqueueIndexed(buildJob, meta);
                _logger.LogInformation("Enqueued build job for imported file '{Path}' in {Org}/{Ds}",
                    target, request.OrgSlug, request.DsSlug);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enqueue build job for imported file '{Path}'", target);
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
            _logger.LogWarning(ex, "Cache invalidation after file import failed for {Org}/{Ds}",
                request.OrgSlug, request.DsSlug);
        }

        progress.Report(new HeavyToolProgress(1, "done", LogChunk: $"Imported {target}"));
        return null;
    }

    // Combines an optional folder with the (already sanitized) file name into a forward-slash
    // relative path.
    private static string BuildTargetPath(string? folder, string fileName)
    {
        var f = (folder ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        return string.IsNullOrEmpty(f) ? fileName : $"{f}/{fileName}";
    }

    // Rejects rooted paths, ".." traversal and the reserved .ddb folder.
    private static void ValidateTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || Path.IsPathRooted(target))
            throw new ArgumentException("Invalid destination path.");

        if (target.Split('/').Any(s => s is "." or ".."))
            throw new ArgumentException("Invalid destination path.");

        if (target.Equals(IDDB.DatabaseFolderName, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(IDDB.DatabaseFolderName + "/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{IDDB.DatabaseFolderName}' is a reserved folder name.");
    }

    private string DecryptPassword(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw.StartsWith("ENC:", StringComparison.Ordinal) ? _protector.Unprotect(raw[4..]) : raw;
    }

    private void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete file-import scratch file '{Path}'", path);
        }
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static bool ReadBool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (!obj.TryGetProperty(name, out var el)) return false;
        return el.ValueKind == JsonValueKind.True;
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
