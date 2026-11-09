using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that merges single-band rasters into a multi-band COG.
/// CPU-bound (GDAL); offloaded from the request thread. Mutates the dataset in
/// place (re-indexes the merged output) so it produces no downloadable artifact.
/// Mirrors the legacy <c>ObjectsManager.MergeMultispectral</c> behaviour exactly. With <c>deleteSources</c> it deletes
/// the band files itself, only after the output is published, indexed and opens as a raster (FR-PRC-02).
/// </summary>
public sealed class MergeMultispectralTool(DatasetPathPolicy pathPolicy) : HeavyToolBase<MergeMultispectralTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string[]? Paths, string? OutputPath, bool? Overwrite, bool? DeleteSources);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.NonBlankStrings("paths"),
        p.Text("outputPath"),
        p.Flag("overwrite"),
        p.Flag("deleteSources"));


    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["paths", "outputPath"],
          "properties": {
            "paths": { "type": "array", "items": { "type": "string" }, "description": "Single-band raster entry paths to merge, in band order." },
            "outputPath": { "type": "string", "description": "Output multi-band COG file name (dataset-relative)." },
            "overwrite": { "type": "boolean", "default": false, "description": "Replace the output file when it exists (without it, an existing output is refused with 409)." },
            "deleteSources": { "type": "boolean", "default": false, "description": "Delete the band files once the merged output is written, indexed and opens as a raster." }
          },
          "additionalProperties": false
        }
        """);

    public override string Id => "merge-multispectral";
    public override string Version => "1";
    public override string Title => "Merge multispectral bands";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public override bool ProducesArtifact => false;
    public override bool RunsNativeBuild => true;

    /// <inheritdoc />
    public override bool AddsDatasetFiles => true;
    public override JsonDocument InputSchema => Schema;

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var paths = args.Paths;
        if (paths == null || paths.Length < 2)
            throw new ArgumentException("At least two band paths are required.");

        var outputPath = args.OutputPath;
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("An output path is required.");

        // libddb's path rule (parity with ObjectsManager.MergeMultispectral)
        outputPath = pathPolicy.Normalize(outputPath, "output path");
        paths = Array.ConvertAll(paths, p => pathPolicy.Normalize(p));

        // Native validation of the band set (CRS/size/type compatibility).
        var json = ctx.Ddb.ValidateMergeMultispectral(paths);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ok", out var okEl) &&
                    okEl.ValueKind == JsonValueKind.False)
                {
                    var errors = ReadErrors(doc.RootElement);
                    throw new ArgumentException(
                        errors.Length > 0
                            ? $"Invalid multispectral merge: {string.Join("; ", errors)}"
                            : "Invalid multispectral merge.");
                }
            }
            catch (JsonException)
            {
                // If the native validation output is not parseable, defer to execution.
            }
        }

        // An existing output is replaced only when the request says so (409 otherwise, D-040); the output an
        // earlier attempt of this task wrote is its own
        if (!PublishedOutputMarker.Covers(ctx.Ddb, ctx.PublishedOutput, outputPath))
            DatasetEntryLookup.ResolveOutput(ctx.Ddb, outputPath, args.Overwrite ?? ctx.QueuedBeforeOverwriteRule);

        return Task.CompletedTask;
    }

    protected override IReadOnlyList<string> InputPaths(Parameters p) =>
        p.Paths is null ? [] : Array.ConvertAll(p.Paths, x => pathPolicy.Normalize(x));

    // The bands go after the commit when the request says so
    protected override IReadOnlyList<string> SourcesDeletedAfterCommit(Parameters p) =>
        p.DeleteSources == true ? InputPaths(p) : [];

    protected override IReadOnlyList<string> OutputPaths(Parameters p) =>
        string.IsNullOrWhiteSpace(p.OutputPath) ? [] : [pathPolicy.Normalize(p.OutputPath, "output path")];

    protected override string? Label(Parameters p) =>
        string.IsNullOrWhiteSpace(p.OutputPath) ? null : "\u2192 " + pathPolicy.Normalize(p.OutputPath, "output path");

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
    {
        long? estimate = null;
        try
        {
            var paths = args.Paths ?? [];
            long total = 0;
            foreach (var p in paths)
            {
                var entry = ctx.Ddb.GetEntry(p);
                if (entry is { Size: > 0 }) total += entry.Size;
            }
            if (total > 0) estimate = total;
        }
        catch
        {
            // best-effort estimate
        }

        return new HeavyToolPlan(estimate, QuotaKey: "merge-multispectral",
            DefaultFileName: null, ContentType: null, ReplacedBytes: ReplacedBytes(args, ctx));
    }

    // The size the owner's use counts for the file the output replaces (overwrite): the storage quota checks only the
    // growth (FR-XC-26). Best-effort, as the estimate
    private long ReplacedBytes(Parameters args, IHeavyToolValidationContext ctx)
    {
        if (!(args.Overwrite ?? ctx.QueuedBeforeOverwriteRule) || string.IsNullOrWhiteSpace(args.OutputPath))
            return 0;
        try
        {
            var outputPath = DatasetEntryLookup.ResolveOutput(ctx.Ddb,
                pathPolicy.Normalize(args.OutputPath, "output path"), overwrite: true);
            return WorkerStorageQuota.IndexedBytes(ctx.Ddb, outputPath);
        }
        catch
        {
            return 0;
        }
    }

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        // Validated at submit; normalized again on the worker (a job enqueued by an older version)
        var paths = Array.ConvertAll(args.Paths
                    ?? throw new InvalidOperationException("Band paths are required."), p => pathPolicy.Normalize(p));
        var outputPath = pathPolicy.Normalize(args.OutputPath, "output path");

        var published = ctx.PublishedOutput;
        if (PublishedOutputMarker.Covers(ctx.Ddb, published, outputPath))
        {
            // An earlier attempt wrote the output and failed after it (the index was busy): index it, never write again
            outputPath = published!;
            progress.Report(new HeavyToolProgress(-1, "indexing",
                LogChunk: $"'{outputPath}' was written by an earlier attempt"));
        }
        else
        {
            // Checked again on the worker: a file may have appeared since the submit. A name that differs only in
            // letter case is the existing file (D-040)
            outputPath = DatasetEntryLookup.ResolveOutput(ctx.Ddb, outputPath, args.Overwrite ?? ctx.QueuedBeforeOverwriteRule);

            progress.Report(new HeavyToolProgress(-1, "merging",
                LogChunk: $"Merging {paths.Length} bands → {outputPath}"));

            // NOTE: libddb 2.0 refuses an existing output: the request asked for the replace. Decided BEFORE that delete:
            // an output the merge replaces is never recorded (FR-XC-27, FR-XC-07), a rollback must not delete the file
            // that was there (and cannot bring the replaced one back)
            var outputFullPath = ctx.Ddb.GetLocalPath(outputPath);
            var replacing = (args.Overwrite ?? ctx.QueuedBeforeOverwriteRule) && File.Exists(outputFullPath);
            if (!replacing && File.Exists(outputFullPath))
                throw new NameClashException(outputPath);
            // What the owner's use counts for the file the merge replaces, read before the run: only the growth counts
            // (FR-XC-26)
            var replacedBytes = replacing ? WorkerStorageQuota.IndexedBytes(ctx.Ddb, outputPath) : 0;
            var root = ctx.Ddb.DatasetFolderPath;
            var writes = await TaskImportWrites.StartAsync(ctx.WriteRecord, ctx.Dataset, root, ct);

            // A new output (and the folders it needs) is in the saved record before it is written (§8.1)
            if (!replacing)
            {
                TaskImportWrites.RecordFolders(writes, root, TaskImportWrites.ParentOf(outputPath));
                writes.Record(outputPath);
                await writes.SaveAsync(ct);
                TaskImportWrites.CreateFolder(root, TaskImportWrites.ParentOf(outputPath));
            }

            // A cancel that came meanwhile stops here, before the file that was there is deleted
            ct.ThrowIfCancellationRequested();
            if (replacing)
                File.Delete(outputFullPath);

            // Created by another writer while the record was saved: kept, and not the task's
            if (!replacing && File.Exists(outputFullPath))
            {
                await writes.ForgetAsync(outputPath, CancellationToken.None);
                throw new NameClashException(outputPath);
            }

            // A failed run keeps the output recorded: a partial file that libddb wrote before it failed is the task's,
            // and the rollback deletes it (FR-XC-27)
            await Task.Run(() => ctx.Ddb.MergeMultispectral(paths, outputPath), ct);

            // The owner of the organization must have room for the growth of the output (FR-XC-26): the task fails with
            // the 507 text
            await EnsureOutputFitsAsync(ctx, request.OrgSlug, outputPath, outputFullPath, replacedBytes, replacing, progress, ct);

            if (!replacing)
                writes.Written(outputPath);

            // The very next step after the write: from here a later attempt indexes the output, and no
            // cancellation check stands between the write and the index. NOTE: a process crash between the
            // write and this line leaves the file without the marker (the retry then refuses it as existing)
            ctx.MarkOutputPublished(outputPath);
        }

        progress.Report(new HeavyToolProgress(-1, "indexing", LogChunk: "Indexing merged output"));
        ctx.Ddb.AddRaw(outputPath);

        if (args.DeleteSources == true)
            await DeleteSourcesAsync(ctx, paths, outputPath, progress, ct);

        progress.Report(new HeavyToolProgress(1, "done", LogChunk: "Merge complete"));
        return null;
    }

    // The storage quota of the owner of the organization against the growth of the merged output, read when it is
    // written. An output that replaced a file is never deleted (FR-XC-27: a rollback never deletes a path that existed
    // before the task, FR-XC-07): it stays, and it is indexed so that the entry describes the file on disk
    private static async Task EnsureOutputFitsAsync(IHeavyToolExecutionContext ctx, string orgSlug, string outputPath,
        string outputFullPath, long replacedBytes, bool replacing, IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        try
        {
            await WorkerStorageQuota.EnsureOutputFitsAsync(ctx.Storage, orgSlug, outputFullPath, replacedBytes, ct);
        }
        catch (QuotaExceededException) when (replacing)
        {
            // The 507 is the reason the task stops: an index error here must not replace it (a busy index
            // would make the job retry and write the output again). The rescan corrects the entry later.
            try
            {
                ctx.Ddb.AddRaw(outputPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress.Report(new HeavyToolProgress(-1, "indexing",
                        LogChunk: $"Could not index the replaced output {outputPath} again: {ex.Message}"));
            }
            throw;
        }
    }

    /// <summary>
    /// Deletes the band files (index entry, then file) once the merged output opens as a raster (GDAL): a failed
    /// open throws and keeps every band (the task is rolled back: the output is deleted). Then the task commits
    /// (FR-XC-27: from here it is never cancelled nor rolled back, and a cancel that came first refuses the commit, which
    /// throws before anything is deleted). Runs after the output is marked published and indexed, with no cancellation
    /// check from the first delete on. Each band is found as the dataset names it (letter case ignored, D-040); a band
    /// that is already gone (a retry of an attempt that deleted it) is skipped, and one that cannot be deleted (a locked
    /// file) is logged and left, as the delete of entries does: the merge itself succeeded.
    /// The bands that left the dataset are told to it as a rollback does (<see cref="ITaskWriteRecorder.EntriesLeftAsync"/>).
    /// </summary>
    private static async Task DeleteSourcesAsync(IHeavyToolExecutionContext ctx, string[] paths, string outputPath,
        IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        var ddb = ctx.Ddb;
        try
        {
            ddb.GetRasterInfo(outputPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The merged output '{outputPath}' cannot be opened as a raster: the source bands were kept ({ex.Message})", ex);
        }

        // Commits before the first delete: a cancel that came first refuses it (the exception ends the task, which is
        // rolled back, and the bands are all still there)
        await ctx.WriteRecord.MarkCommittedAsync(ct);

        progress.Report(new HeavyToolProgress(-1, "cleanup", LogChunk: $"Deleting {paths.Length} source bands"));
        var left = new List<string>();
        foreach (var path in paths)
        {
            // The band as the dataset holds it (indexed, or a file the index no longer lists)
            if (DatasetEntryLookup.Locate(ddb, path).Existing is not { IsFolder: false } band)
                continue;

            try
            {
                if (ddb.EntryExists(band.Path))
                    ddb.Remove(band.Path);
                var local = ddb.GetLocalPath(band.Path);
                if (File.Exists(local))
                    File.Delete(local);
                left.Add(band.Path);
            }
            catch (Exception ex)
            {
                progress.Report(new HeavyToolProgress(-1, "cleanup",
                    LogChunk: $"Could not delete source band '{band.Path}': {ex.Message}"));
            }
        }

        if (left.Count > 0)
            await ctx.WriteRecord.EntriesLeftAsync(ctx.Dataset, left);
    }

    private static string[] ReadErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errEl) || errEl.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>();
        foreach (var item in errEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
            }
        }
        return [.. list];
    }


}
