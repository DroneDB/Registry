#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that packages dataset entries (a selection or the whole dataset)
/// into a downloadable ZIP archive (spec §A.1). Offloads the bulk-download work
/// from the request thread; small selections / single files keep using the legacy
/// direct streaming path. Produces exactly one <c>.zip</c> in the work directory,
/// which the generic <c>GET /tasks/{id}/result</c> endpoint serves as-is.
/// </summary>
public sealed class BulkDownloadTool : IHeavyTool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "paths": {
              "type": ["array", "null"],
              "items": { "type": "string" },
              "description": "Entry paths to include. Omit or leave empty to archive the whole dataset (includes the .ddb database folder)."
            },
            "archiveName": { "type": ["string", "null"], "description": "Optional output archive file name." }
          },
          "additionalProperties": false
        }
        """);

    private readonly IZipArchiveBuilder _zip;

    public BulkDownloadTool(IZipArchiveBuilder zip) => _zip = zip;

    public string Id => "bulk-download";
    public string Version => "1";
    public string Title => "Download archive (ZIP)";
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Read;
    public bool ProducesArtifact => true;
    public string? ResultExtension => "zip";
    public JsonDocument InputSchema => Schema;

    public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var paths = ReadPaths(request.Params);
        if (paths != null)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p) || p.Contains('*') || p.Contains('?'))
                    throw new ArgumentException("Wildcards or empty paths are not supported.");
                if (!ctx.Ddb.EntryExists(p))
                    throw new ArgumentException($"Invalid path: '{p}'");
            }
        }

        var archiveName = ReadString(request.Params, "archiveName");
        if (!string.IsNullOrWhiteSpace(archiveName) &&
            (archiveName.Contains('/') || archiveName.Contains('\\')))
            throw new ArgumentException("archiveName must be a file name without path separators.");

        return Task.CompletedTask;
    }

    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
    {
        var paths = ReadPaths(request.Params);
        long estimate = 0;
        try
        {
            var (files, _, _) = _zip.ExpandPaths(ctx.Ddb, paths);
            estimate = SumFileSizes(ctx, files);
        }
        catch
        {
            // estimate is best-effort
        }

        var name = ResolveArchiveName(request);
        return new HeavyToolPlan(estimate > 0 ? estimate : null, QuotaKey: "bulk-download",
            DefaultFileName: name, ContentType: "application/zip");
    }

    public async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        if (ctx.WorkDir is null)
            throw new InvalidOperationException("BulkDownloadTool requires a work directory.");

        var paths = ReadPaths(request.Params);
        var archiveName = ResolveArchiveName(request);
        var (files, folders, includeDdb) = _zip.ExpandPaths(ctx.Ddb, paths);

        var total = SumFileSizes(ctx, files);
        var outputPath = Path.Combine(ctx.WorkDir, archiveName);

        progress.Report(new HeavyToolProgress(total > 0 ? 0 : -1, "archiving",
            LogChunk: $"Archiving {files.Length} file(s){(includeDdb ? " (whole dataset)" : "")}"));

        var lastPct = -1;
        var byteProgress = new SyncProgress<long>(written =>
        {
            if (total <= 0)
                return; // indeterminate; phase line already emitted
            var pct = (int)(Math.Clamp((double)written / total, 0, 1) * 100);
            if (pct == lastPct) return;
            lastPct = pct;
            progress.Report(new HeavyToolProgress(pct / 100.0, "archiving"));
        });

        await using (var fs = File.Create(outputPath))
        {
            await _zip.WriteZipAsync(ctx.Ddb, files, folders, includeDdb, fs, byteProgress, ct);
        }

        ct.ThrowIfCancellationRequested();

        var info = new FileInfo(outputPath);
        if (!info.Exists)
            throw new InvalidOperationException("Bulk download produced no archive file.");

        var sha = await ComputeSha256Async(outputPath, ct);
        progress.Report(new HeavyToolProgress(1, "done", LogChunk: $"Archive complete ({info.Length} bytes)"));

        return new HeavyToolArtifact(
            RelativePath: archiveName,
            ContentType: "application/zip",
            FileName: archiveName,
            SizeBytes: info.Length,
            Sha256: sha);
    }

    private static long SumFileSizes(IHeavyToolValidationContext ctx, string[] files)
    {
        long total = 0;
        foreach (var f in files)
        {
            try
            {
                var fi = new FileInfo(ctx.Ddb.GetLocalPath(f));
                if (fi.Exists) total += fi.Length;
            }
            catch
            {
                // best-effort
            }
        }
        return total;
    }

    private string ResolveArchiveName(HeavyToolRequest request)
    {
        var name = ReadString(request.Params, "archiveName");
        if (string.IsNullOrWhiteSpace(name))
            return $"{request.DsSlug}.zip";

        name = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(name))
            return $"{request.DsSlug}.zip";

        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name += ".zip";

        return name;
    }

    private static string[]? ReadPaths(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty("paths", out var el) || el.ValueKind != JsonValueKind.Array) return null;

        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
        }

        return list.Count > 0 ? list.ToArray() : null;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> that invokes the handler on the
    /// reporting thread. Avoids the thread-pool dispatch (and ordering hazards)
    /// of <see cref="System.Progress{T}"/> when there is no synchronization context
    /// (Hangfire worker thread).
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
