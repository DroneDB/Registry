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
/// Native tool that makes the black (or white) border pixels of a georaster transparent and writes the result as a new
/// COG next to the source, <c>&lt;name&gt;_masked.tif</c> (FR-PRC-12, D-013). The source is never modified. The output is
/// indexed and its build is queued as a child of the task (its own task row, under its own name); the heavy-task checks
/// of the runner apply to it as to any tool: the output is not an input, no other active task writes it, the owner of
/// the organization has storage left (<see cref="AddsDatasetFiles"/>, FR-XC-26). An existing output is replaced only
/// with <c>overwrite</c> (409 otherwise, D-040). <c>POST …/mask-borders</c> keeps working for existing clients.
/// </summary>
public sealed class MaskBordersTool(DatasetPathPolicy paths, TaskFileIndexer indexer)
    : HeavyToolBase<MaskBordersTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string? Path, long? Near, bool? White, bool? Overwrite);

    /// <summary>The tool id (<see cref="HeavyToolIds.MaskBorders"/>).</summary>
    public const string ToolId = HeavyToolIds.MaskBorders;

    /// <summary>The default pixel distance from black (or white) that counts as border.</summary>
    public const int DefaultNear = 15;

    private const int MaxNear = 255;

    /// <summary>The mask sidecar GDAL writes next to a raster (<c>&lt;output&gt;.msk</c>).</summary>
    public const string MaskSidecarExtension = ".msk";

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.Text("path"),
        p.WholeNumber("near"),
        p.Flag("white"),
        p.Flag("overwrite"));

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["path"],
          "properties": {
            "path":      { "type": "string", "description": "Dataset-relative path of the georaster whose borders are masked." },
            "near":      { "type": "integer", "minimum": 0, "maximum": 255, "default": 15,
                           "description": "How far from black (or white) a pixel can be and still count as border (0-255)." },
            "white":     { "type": "boolean", "default": false,
                           "description": "Mask the white border instead of the black one." },
            "overwrite": { "type": "boolean", "default": false,
                           "description": "Replace the masked copy when it exists (without it, an existing copy is refused with 409)." }
          },
          "additionalProperties": false
        }
        """);

    public override string Id => ToolId;
    public override string Version => "1";
    public override string Title => "Mask Borders";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public override bool ProducesArtifact => false;
    public override bool RunsNativeBuild => true;

    /// <inheritdoc />
    public override bool AddsDatasetFiles => true;
    public override JsonDocument InputSchema => Schema;

    /// <summary>
    /// The masked copy of the normalized dataset path <paramref name="path"/>: <c>&lt;name&gt;_masked.tif</c> in the same
    /// folder (the one rule of this tool and of the <c>mask-borders</c> endpoint).
    /// </summary>
    public static string MaskedOutputPath(string path)
    {
        var fileName = System.IO.Path.GetFileNameWithoutExtension(path) + "_masked.tif";
        var slash = path.LastIndexOf('/');
        return slash < 0 ? fileName : path[..(slash + 1)] + fileName;
    }

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            throw new ArgumentException("path is required.");
        Near(args);

        var path = paths.Normalize(args.Path, "path");
        var entry = ctx.Ddb.GetEntry(path) ?? throw new NotFoundException($"Cannot find entry: '{path}'");
        if (entry.Type != EntryType.GeoRaster)
            throw new BadRequestException($"Entry '{path}' is not a georaster");

        // An existing copy is replaced only when the request says so (409 otherwise, D-040); the output an earlier
        // attempt of this task wrote is its own
        var outputPath = MaskedOutputPath(path);
        if (!PublishedOutputMarker.Covers(ctx.Ddb, ctx.PublishedOutput, outputPath))
            DatasetEntryLookup.ResolveOutput(ctx.Ddb, outputPath, Overwrite(args));

        return Task.CompletedTask;
    }

    // An absent overwrite is false: the tool is new, no queued task predates the rule
    private static bool Overwrite(Parameters args) => args.Overwrite ?? false;

    private static int Near(Parameters args)
    {
        var near = args.Near ?? DefaultNear;
        if (near is < 0 or > MaxNear)
            throw new ArgumentException($"near must be between 0 and {MaxNear}.");
        return (int)near;
    }

    protected override IReadOnlyList<string> InputPaths(Parameters p) =>
        string.IsNullOrWhiteSpace(p.Path) ? [] : [paths.Normalize(p.Path, "path")];

    protected override IReadOnlyList<string> OutputPaths(Parameters p) =>
        string.IsNullOrWhiteSpace(p.Path) ? [] : [MaskedOutputPath(paths.Normalize(p.Path, "path"))];

    protected override string? Label(Parameters p) =>
        string.IsNullOrWhiteSpace(p.Path) ? null : "→ " + MaskedOutputPath(paths.Normalize(p.Path, "path"));

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
    {
        // The masked copy is a COG of about the size of the source: best-effort estimate
        long? estimate = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(args.Path) && ctx.Ddb.GetEntry(paths.Normalize(args.Path, "path")) is { Size: > 0 } entry)
                estimate = entry.Size;
        }
        catch
        {
            // best-effort estimate
        }

        return new HeavyToolPlan(estimate, QuotaKey: ToolId,
            DefaultFileName: null, ContentType: null, ReplacedBytes: ReplacedBytes(args, ctx));
    }

    // The size the owner's use counts for the copy the output replaces (overwrite): the storage quota checks only the
    // growth (FR-XC-26). Best-effort, as the estimate
    private long ReplacedBytes(Parameters args, IHeavyToolValidationContext ctx)
    {
        if (!Overwrite(args) || string.IsNullOrWhiteSpace(args.Path))
            return 0;
        try
        {
            var outputPath = DatasetEntryLookup.ResolveOutput(ctx.Ddb,
                MaskedOutputPath(paths.Normalize(args.Path, "path")), overwrite: true);
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
        // Validated at submit; normalized again on the worker
        var sourcePath = paths.Normalize(args.Path, "path");
        var near = Near(args);
        var white = args.White ?? false;
        var outputPath = MaskedOutputPath(sourcePath);

        // A replace is indexed whatever a cancel says (libddb's mask cannot be cancelled: once it returns the file is
        // replaced, and its entry must describe it); the cancel is honoured once it is indexed (FR-XC-27)
        var indexCt = ct;
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
            outputPath = DatasetEntryLookup.ResolveOutput(ctx.Ddb, outputPath, Overwrite(args));

            progress.Report(new HeavyToolProgress(-1, "masking",
                LogChunk: $"Masking the borders of '{sourcePath}' → '{outputPath}'"));

            // libddb replaces an existing output only when asked (DDBMaskBordersEx), once the mask is complete: a failed
            // run leaves it as it was (FR-PRC-01). Decided before the call: an output the mask replaces is never
            // recorded (FR-XC-27, FR-XC-07), a rollback must not delete the file that was there
            var outputFullPath = ctx.Ddb.GetLocalPath(outputPath);
            var replacing = Overwrite(args) && File.Exists(outputFullPath);
            if (!replacing && File.Exists(outputFullPath))
                throw new NameClashException(outputPath);
            // What the owner's use counts for the file the mask replaces, read before the run: only the growth counts
            // (FR-XC-26)
            var replacedBytes = replacing ? WorkerStorageQuota.IndexedBytes(ctx.Ddb, outputPath) : 0;
            var root = ctx.Ddb.DatasetFolderPath;
            var writes = await TaskImportWrites.StartAsync(ctx.WriteRecord, ctx.Dataset, root, ct);

            // The mask sidecar libddb may write next to the output (GDAL's .msk)
            var sidecarPath = outputPath + MaskSidecarExtension;
            var sidecarFullPath = outputFullPath + MaskSidecarExtension;

            // A new output (and the folders it needs) is in the saved record before it is written (§8.1), with its
            // sidecar unless that is there already (a rollback never deletes a path that existed before the task,
            // FR-XC-07): a failed or cancelled run leaves no orphan sidecar
            var recordsSidecar = !replacing && !File.Exists(sidecarFullPath);
            if (!replacing)
            {
                TaskImportWrites.RecordFolders(writes, root, TaskImportWrites.ParentOf(outputPath));
                writes.Record(outputPath);
                if (recordsSidecar)
                    writes.Record(sidecarPath);
                await writes.SaveAsync(ct);
                TaskImportWrites.CreateFolder(root, TaskImportWrites.ParentOf(outputPath));
            }

            // A cancel that came meanwhile stops here, before libddb runs
            ct.ThrowIfCancellationRequested();

            // Created by another writer while the record was saved: kept, and not the task's
            if (!replacing && File.Exists(outputFullPath))
            {
                await writes.ForgetAsync(outputPath, CancellationToken.None);
                if (recordsSidecar)
                    await writes.ForgetAsync(sidecarPath, CancellationToken.None);
                throw new NameClashException(outputPath);
            }

            // A failed run keeps the output recorded: a file that appears there is the task's, and the rollback deletes
            // it (FR-XC-27). Without the replace, libddb refuses a file that another writer created meanwhile
            await Task.Run(() => ctx.Ddb.MaskBorders(sourcePath, outputPath, near, white, overwrite: replacing), ct);

            // From here a replace goes on to its index whatever a cancel says: the file is replaced already
            if (replacing)
                indexCt = CancellationToken.None;

            // The owner of the organization must have room for the growth of the output and its sidecar (FR-XC-26): the
            // task fails with the 507 text. An output that replaced a file is never deleted (FR-XC-27): it stays, and it
            // is indexed so that the entry describes the file
            try
            {
                await WorkerStorageQuota.EnsureOutputFitsAsync(ctx.Storage, request.OrgSlug,
                    [outputFullPath, sidecarFullPath], replacedBytes, indexCt);
            }
            catch (QuotaExceededException) when (replacing)
            {
                // The 507 is the reason the task stops: an index error here must not replace it (a busy index would make
                // the job retry and write the output again). The rescan corrects the entry later.
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

            if (!replacing)
                writes.Written(outputPath);
            if (recordsSidecar && File.Exists(sidecarFullPath))
                writes.Written(sidecarPath);

            // The very next step after the write: from here a later attempt indexes the output, and no cancellation
            // check stands between the write and the index. NOTE: a process crash between the write and this line
            // leaves the file without the marker (the retry then refuses it as existing)
            ctx.MarkOutputPublished(outputPath);
        }

        // Index the masked copy, queue its build as a child of this task (a build task under its own name, FR-XC-16),
        // invalidate the caches
        progress.Report(new HeavyToolProgress(-1, "indexing", LogChunk: "Indexing the masked file"));
        await indexer.IndexAsync(new TaskFiles(ctx.Ddb, ctx.Dataset, request.OrgSlug, request.DsSlug, [outputPath],
            ctx.BuildQueue, ctx.TaskId, "masked file"), progress, indexCt);

        // A cancel that came while a replace ran stops the task now that the entry describes the file
        ct.ThrowIfCancellationRequested();

        progress.Report(new HeavyToolProgress(1, "done", LogChunk: $"Masked file: {outputPath}"));
        return null;
    }
}
