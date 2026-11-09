using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that aligns a source GeoTIFF to a reference GeoTIFF and indexes
/// the output in the dataset. CPU-bound (GDAL); offloaded from the request thread.
/// Mutates the dataset in place (re-indexes the aligned output) – no downloadable
/// artifact is produced. The caller (frontend) is responsible for submitting a
/// separate "build" task for the output file after this task succeeds.
/// </summary>
public sealed class AlignRasterTool(DatasetPathPolicy paths) : HeavyToolBase<AlignRasterTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string? SourcePath, string? ReferencePath, string? OutputPath, string? Mode,
        bool? Overwrite);

    /// <summary>Job parameter that keeps the alignment report of the attempt that wrote the output (<see cref="PublishedOutputMarker"/>).</summary>
    public const string ResultParameter = "alignResult";

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.Text("sourcePath"),
        p.Text("referencePath"),
        p.Text("outputPath"),
        p.Text("mode"),
        p.Flag("overwrite"));


    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["sourcePath", "referencePath", "outputPath"],
          "properties": {
            "sourcePath":    { "type": "string", "description": "Dataset-relative path of the GeoTIFF to align." },
            "referencePath": { "type": "string", "description": "Dataset-relative path of the reference GeoTIFF." },
            "outputPath":    { "type": "string", "description": "Dataset-relative path for the aligned output (a lossless GeoTIFF in the source projection and grid)." },
            "mode":          { "type": "string", "enum": ["similarity", "translation"], "default": "similarity",
                               "description": "Alignment mode: similarity (4-DOF) or translation (2-DOF, fast)." },
            "overwrite":     { "type": "boolean", "default": false,
                               "description": "Replace the output file when it exists (without it, an existing output is refused with 409)." }
          },
          "additionalProperties": false
        }
        """);

    public override string Id => "align-raster";
    public override string Version => "1";
    public override string Title => "Align GeoTIFF to reference";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public override bool ProducesArtifact => false;
    public override bool RunsNativeBuild => true;

    /// <inheritdoc />
    public override bool AddsDatasetFiles => true;
    public override JsonDocument InputSchema => Schema;

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var sourcePath = args.SourcePath;
        var referencePath = args.ReferencePath;
        var outputPath = args.OutputPath;

        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath is required.");
        if (string.IsNullOrWhiteSpace(referencePath))
            throw new ArgumentException("referencePath is required.");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("outputPath is required.");

        sourcePath = paths.Normalize(sourcePath, "source path");
        referencePath = paths.Normalize(referencePath, "reference path");
        outputPath = paths.Normalize(outputPath, "output path");

        // Native validation (CRS, overlap, type compatibility)
        ValidateNative(ctx, sourcePath, referencePath);

        // An existing output is replaced only when the request says so (409 otherwise, D-040); the output an
        // earlier attempt of this task wrote is its own
        if (!PublishedOutputMarker.Covers(ctx.Ddb, ctx.PublishedOutput, outputPath))
            DatasetEntryLookup.ResolveOutput(ctx.Ddb, outputPath, Overwrite(args, ctx));

        return Task.CompletedTask;
    }

    // An absent overwrite is false, except in a task queued before the overwrite rule, which replaced its output
    private static bool Overwrite(Parameters args, IHeavyToolValidationContext ctx) =>
        args.Overwrite ?? ctx.QueuedBeforeOverwriteRule;

    private static void ValidateNative(IHeavyToolValidationContext ctx, string sourcePath, string referencePath)
    {
        var validationJson = ctx.Ddb.ValidateAlignRaster(sourcePath, referencePath);
        if (string.IsNullOrWhiteSpace(validationJson)) return;

        try
        {
            using var doc = JsonDocument.Parse(validationJson);
            if (doc.RootElement.TryGetProperty("ok", out var okEl) &&
                okEl.ValueKind == JsonValueKind.False)
            {
                var errors = ReadErrors(doc.RootElement);
                throw new ArgumentException(
                    errors.Length > 0
                        ? $"Alignment not possible: {string.Join("; ", errors)}"
                        : "Alignment validation failed.");
            }
        }
        catch (JsonException)
        {
            // If native output is not parseable, defer to execution phase.
        }
    }

    protected override IReadOnlyList<string> InputPaths(Parameters p) =>
    [
        .. new[] { (Path: p.SourcePath, Subject: "source path"), (Path: p.ReferencePath, Subject: "reference path") }
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .Select(x => paths.Normalize(x.Path, x.Subject))
    ];

    protected override IReadOnlyList<string> OutputPaths(Parameters p) =>
        string.IsNullOrWhiteSpace(p.OutputPath) ? [] : [paths.Normalize(p.OutputPath, "output path")];

    protected override string? Label(Parameters p) =>
        string.IsNullOrWhiteSpace(p.OutputPath) ? null : "\u2192 " + paths.Normalize(p.OutputPath, "output path");

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
    {
        long? estimate = null;
        try
        {
            var sourcePath = args.SourcePath;
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                var entry = ctx.Ddb.GetEntry(sourcePath);
                if (entry is { Size: > 0 }) estimate = entry.Size;
            }
        }
        catch
        {
            // best-effort estimate
        }

        return new HeavyToolPlan(estimate, QuotaKey: "align-raster",
            DefaultFileName: null, ContentType: null);
    }

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        // Validated at submit; normalized again on the worker (a job enqueued by an older version)
        var sourcePath = paths.Normalize(args.SourcePath, "source path");
        var referencePath = paths.Normalize(args.ReferencePath, "reference path");
        var outputPath = paths.Normalize(args.OutputPath, "output path");
        var mode = args.Mode ?? "similarity";

        string compact;
        var published = ctx.PublishedOutput;
        if (PublishedOutputMarker.Covers(ctx.Ddb, published, outputPath))
        {
            // An earlier attempt wrote the output and failed after it (the index was busy): index it, never write again
            outputPath = published!;
            compact = ctx.Hangfire?.Connection?.GetJobParameter(ctx.TaskId, ResultParameter) ?? "{}";
            progress.Report(new HeavyToolProgress(-1, "indexing",
                LogChunk: $"'{outputPath}' was written by an earlier attempt"));
        }
        else
        {
            // Checked again on the worker: a file may have appeared since the submit. A name that differs only in
            // letter case is the existing file (D-040)
            outputPath = DatasetEntryLookup.ResolveOutput(ctx.Ddb, outputPath, Overwrite(args, ctx));

            progress.Report(new HeavyToolProgress(-1, "aligning",
                LogChunk: $"Aligning '{sourcePath}' → '{outputPath}' (mode: {mode})"));

            // NOTE: libddb 2.0 writes the output in place and needs it gone: the request asked for the replace. Decided
            // BEFORE that delete: an output the alignment replaces is never recorded (FR-XC-27, FR-XC-07), a rollback
            // must not delete the file that was there (and cannot bring the replaced one back)
            var outputFullPath = ctx.Ddb.GetLocalPath(outputPath);
            var replacing = Overwrite(args, ctx) && File.Exists(outputFullPath);
            if (!replacing && File.Exists(outputFullPath))
                throw new NameClashException(outputPath);
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

            ct.ThrowIfCancellationRequested();

            // Created by another writer while the record was saved: kept, and not the task's
            if (!replacing && File.Exists(outputFullPath))
            {
                await writes.ForgetAsync(outputPath, CancellationToken.None);
                throw new NameClashException(outputPath);
            }

            // A failed run keeps the output recorded: a partial file that libddb wrote before it failed is the task's,
            // and the rollback deletes it (FR-XC-27)
            var resultJson = await Task.Run(
                () => ctx.Ddb.AlignRaster(sourcePath, referencePath, outputPath, mode), ct);

            if (!replacing)
                writes.Written(outputPath);

            // The very next step after the write: from here a later attempt indexes the output, and no
            // cancellation check stands between the write and the index. NOTE: a process crash between the
            // write and this line leaves the file without the marker (the retry then refuses it as existing)
            ctx.MarkOutputPublished(outputPath);

            // Emit compact (single-line) JSON so the log tail delivers it as a single
            // entry that the frontend can JSON.parse after stripping the timestamp prefix.
            using (var doc = JsonDocument.Parse(resultJson))
                compact = JsonSerializer.Serialize(doc);
            ctx.Hangfire?.Connection?.SetJobParameter(ctx.TaskId, ResultParameter, compact);
        }

        progress.Report(new HeavyToolProgress(-1, "indexing", LogChunk: "Indexing aligned output"));
        ctx.Ddb.AddRaw(outputPath);

        progress.Report(new HeavyToolProgress(1, "done", LogChunk: compact));
        return null;
    }

    private static string[] ReadErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errEl) || errEl.ValueKind != JsonValueKind.Array)
            return [];

        return
        [
            .. from item in errEl.EnumerateArray()
            where item.ValueKind == JsonValueKind.String
            select item.GetString()
            into s
            where !string.IsNullOrWhiteSpace(s)
            select s!
        ];
    }

}