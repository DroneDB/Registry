using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that packages dataset entries (a selection or the whole dataset)
/// into a downloadable ZIP archive. Offloads the bulk-download work
/// from the request thread; small selections / single files keep using the legacy
/// direct streaming path. Produces exactly one <c>.zip</c> in the work directory,
/// which the generic <c>GET /tasks/{id}/result</c> endpoint serves as-is.
/// </summary>
public sealed class BulkDownloadTool : HeavyToolBase<BulkDownloadTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string[]? Paths, string? ArchiveName);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.NonBlankStrings("paths"),
        p.Text("archiveName"));


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

    public override string Id => "bulk-download";
    public override string Version => "1";
    public override string Title => "Download archive (ZIP)";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Read;
    public override bool ProducesArtifact => true;
    public override string? ResultExtension => "zip";
    public override JsonDocument InputSchema => Schema;

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var paths = args.Paths;
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

        var archiveName = args.ArchiveName;
        if (!string.IsNullOrWhiteSpace(archiveName) &&
            (archiveName.Contains('/') || archiveName.Contains('\\')))
            throw new ArgumentException("archiveName must be a file name without path separators.");

        return Task.CompletedTask;
    }

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
    {
        var paths = args.Paths;
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

        var name = ResolveArchiveName(request, args);
        return new HeavyToolPlan(estimate > 0 ? estimate : null, QuotaKey: "bulk-download",
            DefaultFileName: name, ContentType: "application/zip");
    }

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        if (ctx.WorkDir is null)
            throw new InvalidOperationException("BulkDownloadTool requires a work directory.");

        var paths = args.Paths;
        var archiveName = ResolveArchiveName(request, args);
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

        // Hashed while it is written (the zip writer never seeks): the archive is not read back
        string sha;
        await using (var archive = new HashingWriteStream(File.Create(outputPath)))
        {
            await _zip.WriteZipAsync(ctx.Ddb, files, folders, includeDdb, archive, byteProgress, ct);
            sha = archive.GetHexDigest();
        }

        ct.ThrowIfCancellationRequested();

        var info = new FileInfo(outputPath);
        if (!info.Exists)
            throw new InvalidOperationException("Bulk download produced no archive file.");

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

    private static string ResolveArchiveName(HeavyToolRequest request, Parameters args)
    {
        var name = args.ArchiveName;
        if (string.IsNullOrWhiteSpace(name))
            return $"{request.DsSlug}.zip";

        name = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(name))
            return $"{request.DsSlug}.zip";

        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name += ".zip";

        return name;
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
