using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Import;
using Registry.Web.Services.Jobs;
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

    private readonly FileUrlTransfer _transfer;
    private readonly ImportCredentials _credentials;
    private readonly TaskFileIndexer _indexer;
    private readonly ImportSettings _importSettings;
    private readonly DatasetPathPolicy _paths;
    private readonly IFileSystem _fs;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileUrlImportTool"/> class.
    /// </summary>
    /// <param name="transfer">Downloads the file into place (it also pre-validates the host at submit time).</param>
    /// <param name="credentials">Decrypts the basic-auth password.</param>
    /// <param name="indexer">Indexes the imported file, queues its build and invalidates the dataset's caches.</param>
    /// <param name="importSettings">The import settings (the extension policy).</param>
    /// <param name="paths">The dataset path rule (destination folder and file).</param>
    /// <param name="fs">The file system the dataset folder is read from.</param>
    public FileUrlImportTool(
        FileUrlTransfer transfer,
        ImportCredentials credentials,
        TaskFileIndexer indexer,
        IOptions<ImportSettings> importSettings,
        DatasetPathPolicy paths,
        IFileSystem fs)
    {
        _transfer = transfer;
        _paths = paths;
        _credentials = credentials;
        _indexer = indexer;
        _importSettings = importSettings.Value;
        _fs = fs;
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
    public override bool AddsDatasetFiles => true;

    /// <inheritdoc />
    public override JsonDocument InputSchema => Schema;

    /// <inheritdoc />
    protected override async Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var uri = FileImportPolicy.ParseHttpUrl(args.Url);

        // Fast SSRF rejection at submit time (re-validated at connect time during download).
        await _transfer.AssertHostAllowedAsync(uri.Host, ct);

        var fileName = FileImportPolicy.SanitizeFileName(args.FileName);
        if (!_importSettings.IsExtensionAllowed(fileName))
            throw new ArgumentException($"Files of type '{Path.GetExtension(fileName)}' are not allowed.");

        var target = TargetPath(args.Folder, fileName);

        // An existing file is replaced only when the request says so (409 otherwise, D-040); the file an
        // earlier attempt of this task moved into place is its own
        if (!PublishedOutputMarker.Covers(ctx.Ddb, _fs, ctx.PublishedOutput, target))
            DatasetEntryLookup.ResolveOutput(ctx.Ddb, target, args.Overwrite == true);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> OutputPaths(Parameters p) => [TargetPath(p.Folder, FileImportPolicy.SanitizeFileName(p.FileName))];

    /// <inheritdoc />
    protected override string? Label(Parameters p) =>
        "\u2192 " + TargetPath(p.Folder, FileImportPolicy.SanitizeFileName(p.FileName));

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

        var published = ctx.PublishedOutput;
        if (PublishedOutputMarker.Covers(ctx.Ddb, _fs, published, target))
        {
            // An earlier attempt moved the file into place and failed after it: index it, never download it again
            target = published!;
            progress.Report(new HeavyToolProgress(-1, "indexing",
                LogChunk: $"'{target}' was imported by an earlier attempt"));
        }
        else
        {
            // Checked again on the worker: a file may have appeared since the submit. A name that differs only in
            // letter case is the existing file (D-040)
            target = DatasetEntryLookup.ResolveOutput(ctx.Ddb, target, args.Overwrite == true);
            await _transfer.DownloadIntoPlaceAsync(new FileUrlDownload(uri, fileName, target, args.Overwrite == true,
                args.Username, DecryptPassword(args.Password)), request.OrgSlug, ctx, progress, ct);
        }

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
}
