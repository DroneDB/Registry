#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that exports the outputs of an ODM project in a dataset folder as the training data of a
/// Gaussian splat (<c>DDBExportSplatTrainingData</c>): a COLMAP sparse model in meters (<c>sparse/0/*.bin</c>), the
/// images (<c>images/</c>) and the georeference of the project, which makes the splat trained from it georeferenced when it is
/// uploaded next to the project. The result is one ZIP the generic <c>GET /tasks/{id}/result</c> endpoint serves;
/// what the user should know before training (images left out, an approximated lens, a flight that looks straight
/// down) goes to the log of the task. Read access, like the other exports: it only re-packages files the caller can
/// already download.
/// </summary>
public sealed class SplatTrainingExportTool : HeavyToolBase<SplatTrainingExportTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(long? MaxImageSize, long? MaxPoints, string? FileName);

    // The native export runs first; archiving the result is the last tenth of the progress
    private const double ExportShare = 0.9;
    private const string ExportFolderName = "training-data";

    protected override Parameters ReadParams(ToolParameters p) =>
        new(p.WholeNumber("maxImageSize"), p.WholeNumber("maxPoints"), p.Text("fileName"));

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "path": { "type": ["string", "null"], "description": "Folder of the ODM project (cameras.json, odm_report, images, odm_georeferencing); empty for the dataset root." },
            "maxImageSize": { "type": ["integer", "null"], "minimum": 0, "description": "Longest side in pixels of the exported images; 0 or absent exports the originals." },
            "maxPoints": { "type": ["integer", "null"], "minimum": 0, "description": "Most points of the sparse model (default 100000); 0 writes none." },
            "fileName": { "type": ["string", "null"], "description": "Optional output file name." }
          },
          "additionalProperties": false
        }
        """);

    private readonly IZipArchiveBuilder _zip;
    private readonly IFileSystem _fs;

    public SplatTrainingExportTool(IZipArchiveBuilder zip, IFileSystem fs)
    {
        _zip = zip;
        _fs = fs;
    }

    public override string Id => "splat-training-export";
    public override string Version => "1";
    public override string Title => "Export splat training data";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Read;
    public override bool ProducesArtifact => true;

    // The export decodes images and thins the point cloud for minutes: it shares the builds queue with the
    // other native work, whose worker count bounds how many run at once
    public override bool RunsNativeBuild => true;
    public override string? ResultExtension => "zip";
    public override JsonDocument InputSchema => Schema;

    protected override Task ValidateAsync(HeavyToolRequest request, Parameters p, IHeavyToolValidationContext ctx,
        CancellationToken ct)
    {
        Require(p.MaxImageSize, nameof(p.MaxImageSize));
        Require(p.MaxPoints, nameof(p.MaxPoints));

        if (!IsRoot(request.Path))
        {
            var entry = ctx.Ddb.GetEntry(request.Path!)
                        ?? throw new NotFoundException($"Folder '{request.Path}' does not exist in the dataset.");
            if (entry.Type != EntryType.Directory)
                throw new BadRequestException($"'{request.Path}' is not a folder: give the folder of the ODM project.");
        }

        return Task.CompletedTask;
    }

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters p, IHeavyToolValidationContext ctx) =>
        // The size depends on the images the project holds and on maxImageSize: no cheap estimate
        new(null, QuotaKey: Id, DefaultFileName: FileName(request, p), ContentType: "application/zip");

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(HeavyToolRequest request, Parameters p,
        IHeavyToolExecutionContext ctx, IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        if (ctx.WorkDir is null)
            throw new InvalidOperationException($"{nameof(SplatTrainingExportTool)} requires a work directory.");

        var fileName = FileName(request, p);
        var exportFolder = Path.Combine(ctx.WorkDir, ExportFolderName);
        var archivePath = Path.Combine(ctx.WorkDir, fileName);
        var project = IsRoot(request.Path) ? "." : request.Path!;
        var options = new SplatTrainingOptions((int?)p.MaxImageSize, (int?)p.MaxPoints);

        progress.Report(new HeavyToolProgress(0, "cameras", LogChunk: $"Exporting the training data of '{project}'"));

        var lastPhase = "cameras";
        var export = await progress.ReportingFailureAsync(() => Task.Run(() => ctx.Ddb.ExportSplatTrainingData(project,
            exportFolder, options,
            (fraction, phase) =>
            {
                // One log line per phase (cameras, points, images, model), not one per report
                var logChunk = !string.IsNullOrEmpty(phase) && phase != lastPhase ? $"Phase: {phase}" : null;
                lastPhase = phase ?? lastPhase;
                progress.Report(new HeavyToolProgress(fraction * ExportShare, phase, LogChunk: logChunk));
            }, ct), ct));

        // What the user should know before training goes to the log, one line each
        foreach (var warning in export.Warnings)
            progress.Report(new HeavyToolProgress(ExportShare, "warnings", LogChunk: $"Warning: {warning}"));

        progress.Report(new HeavyToolProgress(ExportShare, "archiving",
            LogChunk: $"{export.Images} image(s), {export.Points} point(s): archiving"));

        // Hashed while it is written (the zip writer never seeks): the archive is not read back
        string sha;
        await using (var archive = new HashingWriteStream(_fs.OpenWrite(archivePath)))
        {
            await _zip.WriteFolderZipAsync(exportFolder, archive, ct);
            sha = archive.GetHexDigest();
        }

        ct.ThrowIfCancellationRequested();

        // The model is in the archive: the task's folder keeps the one file it serves for its lifetime
        _fs.FolderDelete(exportFolder);

        var info = new FileInfo(archivePath);
        progress.Report(new HeavyToolProgress(1, "done", LogChunk: $"Export complete ({info.Length} bytes)"));

        return new HeavyToolArtifact(RelativePath: fileName, ContentType: "application/zip", FileName: fileName,
            SizeBytes: info.Length, Sha256: sha);
    }

    // The root of the dataset is "", "." or no path
    private static bool IsRoot(string? path) => string.IsNullOrWhiteSpace(path) || path.Trim() == ".";

    private static void Require(long? value, string name)
    {
        if (value is < 0 or > int.MaxValue)
            throw new ArgumentException($"{char.ToLowerInvariant(name[0])}{name[1..]} must be a whole number from 0 to {int.MaxValue}.");
    }

    private static string FileName(HeavyToolRequest request, Parameters p)
    {
        var folder = IsRoot(request.Path) ? request.DsSlug : Path.GetFileName(request.Path!.TrimEnd('/'));
        var name = ToolFileNames.Safe(p.FileName, $"{folder}-training-data.zip");
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name : name + ".zip";
    }
}
