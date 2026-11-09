using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.NodeOdx;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Remote photogrammetry tool backed by a NodeODX (OpenDroneMap) processing node.
/// Collects the dataset's images, submits them to NodeODX, streams progress/log,
/// downloads the result bundle (<c>all.zip</c>) and extracts it directly into the
/// dataset (or into a newly created dataset). Extracted files are indexed and
/// per-file build jobs are enqueued automatically. No downloadable artifact is produced.
/// Cooperatively cancellable - cancellation propagates to the remote NodeODX task.
/// </summary>
public sealed class PhotogrammetryTool : HeavyToolBase<PhotogrammetryTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string? NodeId, string? Folder, List<string>? Images, bool? CreateNewDataset, string? DestPath, string? NewDatasetName, string? NewDatasetOrgSlug, string? NewDatasetVisibility, string? Name, string? Options, string? Profile = null);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.Text("nodeId"),
        p.Text("folder"),
        p.Strings("images"),
        p.Flag("createNewDataset"),
        p.Text("destPath"),
        p.Text("newDatasetName"),
        p.Text("newDatasetOrgSlug"),
        p.Text("newDatasetVisibility"),
        p.Text("name"),
        p.RawArray("options"),
        p.Text("profile"));

    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "folder": { "type": ["string", "null"], "description": "Dataset folder to scan for images (default: whole dataset)." },
            "images": { "type": ["array", "null"], "items": { "type": "string" }, "description": "Explicit list of image entry paths." },
            "nodeId": { "type": ["string", "null"], "description": "Target NodeODX node id (default: first configured)." },
            "name": { "type": ["string", "null"], "description": "Task name shown on the node." },
            "profile": { "type": ["string", "null"], "description": "Name of the processing profile the options come from, shown in the task's label." },
            "options": { "type": ["array", "null"], "description": "NodeODX options array [{name,value}]." },
            "destPath": { "type": ["string", "null"], "description": "Destination folder within the dataset for extracted results (required unless createNewDataset is true)." },
            "createNewDataset": { "type": ["boolean", "null"], "description": "If true, create a new dataset for the photogrammetry results instead of extracting into the current dataset." },
            "newDatasetName": { "type": ["string", "null"], "description": "Slug for the new dataset (kebab-case, max 128 chars). Auto-generated if not provided." },
            "newDatasetOrgSlug": { "type": ["string", "null"], "description": "Organization slug for the new dataset (default: same as source dataset)." },
            "newDatasetVisibility": { "type": ["string", "null"], "description": "Visibility for the new dataset: PRIVATE, UNLISTED, or PUBLIC (default: PRIVATE)." }
          },
          "additionalProperties": false
        }
        """);

    private readonly NodeOdxTaskRunner _node;
    private readonly DatasetArchiveExtractor _archives;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcessingPlatformSettings _settings;
    private readonly ImportSettings _importSettings;
    private readonly DatasetPathPolicy _paths;
    private readonly TaskFileIndexer _indexer;

    public PhotogrammetryTool(
        NodeOdxTaskRunner node,
        DatasetArchiveExtractor archives,
        IServiceScopeFactory scopeFactory,
        IOptions<ImportSettings> importSettings, IOptions<ProcessingPlatformSettings> platformSettings,
        DatasetPathPolicy paths,
        TaskFileIndexer indexer)
    {
        _node = node;
        _paths = paths;
        _archives = archives;
        _scopeFactory = scopeFactory;
        _settings = platformSettings.Value;
        _importSettings = importSettings.Value;
        _indexer = indexer;
    }

    public override string Id => "photogrammetry";
    public override string Version => "2";
    public override string Title => "Photogrammetry (NodeODX)";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public override bool ProducesArtifact => false;

    /// <inheritdoc />
    public override bool AddsDatasetFiles => true;
    public override JsonDocument InputSchema => Schema;

    /// <summary>
    /// The label of a run, so that runs can be told apart in the task lists (FR-PRC-22): the task name, the image
    /// folder (or the number of images), the output and the profile, as in
    /// "limit-1 · images/ → photogrammetry_output (Fast Orthophoto)".
    /// </summary>
    protected override string? Label(Parameters p)
    {
        var source = !string.IsNullOrWhiteSpace(p.Folder) ? p.Folder.Trim().TrimEnd('/') + "/"
            : p.Images is { Count: > 0 } images ? $"{images.Count} images" : null;
        var head = string.Join(" \u00b7 ",
            new[] { p.Name?.Trim(), source }.Where(part => !string.IsNullOrEmpty(part)));
        var target = p.CreateNewDataset == true
            ? ("new dataset " + (p.NewDatasetName ?? string.Empty)).Trim()
            : p.DestPath?.Trim();
        var label = string.IsNullOrEmpty(target) ? head : $"{head} \u2192 {target}".Trim();
        if (!string.IsNullOrWhiteSpace(p.Profile))
            label = $"{label} ({p.Profile.Trim()})".Trim();
        return string.IsNullOrWhiteSpace(label) ? null : label;
    }

    /// <summary>
    /// The organization of the new dataset when the results go into one (the quota of its owner applies, D-039), else the
    /// request's organization.
    /// </summary>
    public override string StorageOrganization(HeavyToolRequest request)
    {
        var args = Params(request);
        return args.CreateNewDataset == true && !string.IsNullOrWhiteSpace(args.NewDatasetOrgSlug)
            ? args.NewDatasetOrgSlug
            : request.OrgSlug;
    }

    protected override async Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        if (!_node.HasNodes)
            throw new InvalidOperationException("No NodeODX processing node is configured.");

        var nodeId = args.NodeId;
        if (_node.Resolve(nodeId) is null)
            throw new ArgumentException($"NodeODX node '{nodeId}' is not configured.");

        // Validate the optional image folder path (empty = whole dataset). Reject
        // rooted paths and traversal so a clean dataset-relative path is required.
        _paths.NormalizeFolder(args.Folder);

        // Validate explicit image list before collecting.
        var explicitImages = args.Images;
        if (explicitImages is { Count: > 0 })
        {
            var missing = explicitImages.Where(p => !ctx.Ddb.EntryExists(_paths.Normalize(p))).ToList();
            if (missing.Count > 0)
                throw new ArgumentException(
                    $"The following image paths do not exist in the dataset: {string.Join(", ", missing.Select(p => $"'{p}'"))}");
        }

        var images = CollectImageEntries(request, args, ctx.Ddb);
        if (images.Count < 2)
            throw new ArgumentException("Photogrammetry requires at least 2 images.");

        // Validate output destination parameters.
        var createNewDataset = args.CreateNewDataset ?? false;
        var destPath = args.DestPath ?? string.Empty;

        if (createNewDataset)
        {
            // The slug rule is the Registry's one (SlugRules, FR-XC-12): an omitted name gets a generated slug
            var newDatasetName = args.NewDatasetName;
            var hasName = !string.IsNullOrWhiteSpace(newDatasetName);
            if (hasName && !SlugRules.IsValid(newDatasetName))
                throw new ArgumentException(SlugRules.InvalidMessage("Dataset"));

            var newOrgSlug = args.NewDatasetOrgSlug ?? request.OrgSlug;
            if (!SlugRules.IsValid(newOrgSlug))
                throw new ArgumentException(SlugRules.InvalidMessage("Organization"));

            // Validate visibility.
            var visibilityStr = args.NewDatasetVisibility ?? "PRIVATE";
            if (!Enum.TryParse<Visibility>(visibilityStr, true, out _))
                throw new ArgumentException($"Invalid visibility '{visibilityStr}'. Must be PRIVATE, UNLISTED, or PUBLIC.");

            // The requesting user may create a dataset in that organization: checked at submit (the caller), and again
            // by the worker before it creates the dataset, for the user the task runs for (D-031)
            using var scope = _scopeFactory.CreateScope();
            if (ctx.Caller is not null)
                await scope.ServiceProvider.GetRequiredService<IDatasetAccessResolver>()
                    .ResolveOrganizationAsync(newOrgSlug, AccessType.Write, ct: ct);

            // Fails fast on a slug that is taken (the worker checks again before it creates the dataset, at submit
            // and when the task starts); nothing is created here. The retry of a task whose earlier run created its
            // dataset, still there in that organization, imports into it again (the rule of the worker,
            // ReusableCreatedDatasetAsync): its own dataset is not a taken slug (D-037)
            var creation = scope.ServiceProvider.GetRequiredService<DatasetCreation>();
            if (hasName && !await OwnDatasetExistsAsync(creation, ctx.OwnCreatedDataset, newOrgSlug, ct) &&
                await creation.SlugIsTakenAsync(newOrgSlug, newDatasetName!, ct))
                throw new ConflictException(SlugTakenMessage(newDatasetName!, newOrgSlug));
        }
        else
        {
            // destPath is required when not creating a new dataset.
            if (string.IsNullOrWhiteSpace(destPath))
                throw new ArgumentException("A destination folder path (destPath) is required unless createNewDataset is true.");

            // libddb's path rule
            destPath = _paths.NormalizeFolder(destPath);

            var destEntry = ctx.Ddb.GetEntry(destPath);
            if (destEntry != null && destEntry.Type != EntryType.Directory)
                throw new ArgumentException($"The destination '{destPath}' is not a folder.");
        }
    }

    /// <summary>The text of a task whose requester may not create a dataset in <paramref name="orgSlug"/> (D-031).</summary>
    internal static string NoWriteAccessMessage(string orgSlug) =>
        $"The user who started this task cannot write to the organization \"{orgSlug}\": the results were not imported";

    // The worker's check of the access of the user the task runs for (D-031): the rules of the API for that user, read
    // without a request (IUserDatasetAccess). A task with no requesting user has nobody whose access holds
    private static async Task EnsureRequesterMayCreateAsync(IServiceProvider services, string? requestedBy,
        string orgSlug, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(requestedBy) ||
            !await services.GetRequiredService<IUserDatasetAccess>()
                .CanAccessOrganizationAsync(requestedBy, orgSlug, AccessType.Write, ct))
            throw new ForbiddenException(NoWriteAccessMessage(orgSlug));
    }

    // The dataset an earlier run of the task created and recorded, when it is still there in the target organization
    // (by its folder id; its slug may have changed since): null otherwise
    private static async Task<(Guid InternalRef, string Slug)?> ReusableCreatedDatasetAsync(DatasetCreation creation,
        ITaskWriteRecorder record, string orgSlug, CancellationToken ct)
    {
        if (await record.RecordedCreatedDatasetAsync(ct) is not { } recorded ||
            !string.Equals(recorded.Org, orgSlug, StringComparison.Ordinal))
            return null;

        return await creation.ExistingSlugAsync(orgSlug, recorded.InternalRef, ct) is { } slug
            ? (recorded.InternalRef, slug)
            : null;
    }

    // Whether the dataset an earlier run of the task created (the retry's OwnCreatedDataset) is still there in the target
    // organization, found by its folder id as the worker finds it: the re-run imports into it
    private static async Task<bool> OwnDatasetExistsAsync(DatasetCreation creation, TaskCreatedDataset? own,
        string orgSlug, CancellationToken ct) =>
        own is not null && string.Equals(own.Org, orgSlug, StringComparison.Ordinal) &&
        await creation.ExistingSlugAsync(orgSlug, own.InternalRef, ct) is not null;

    // The one text of a slug that is taken, at submit and in the worker (§7.1)
    private static string SlugTakenMessage(string slug, string orgSlug) =>
        $"A dataset named \"{slug}\" already exists in \"{orgSlug}\"";

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
    {
        long? estimate = null;
        try
        {
            var images = CollectImageEntries(request, args, ctx.Ddb);
            var inputBytes = images.Sum(e => Math.Max(0, e.Size));
            // ODM products (ortho + DSM/DTM + point cloud + model) are typically a
            // fraction of the raw image payload; rough upper-bound heuristic.
            if (inputBytes > 0) estimate = (long)(inputBytes * 0.75);
        }
        catch
        {
            // best-effort estimate
        }

        return new HeavyToolPlan(estimate, QuotaKey: "archive-extract",
            DefaultFileName: null, ContentType: null);
    }

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        // --- Step 1: Submit to NodeODX and download results ---
        var nodeId = args.NodeId;
        var node = _node.Resolve(nodeId)
                   ?? throw new InvalidOperationException($"NodeODX node '{nodeId}' is not configured.");

        var entries = CollectImageEntries(request, args, ctx.Ddb);
        if (entries.Count < 2)
            throw new InvalidOperationException("Photogrammetry requires at least 2 images.");

        var imagePaths = entries.Select(e => ctx.Ddb.GetLocalPath(e.Path)).ToList();
        var taskName = args.Name ?? $"{request.OrgSlug}/{request.DsSlug}";
        var optionsJson = args.Options;

        // Submit, follow the node, download the result bundle (deleted at the end)
        using var bundle = await _node.RunAsync(node, taskName, imagePaths, optionsJson, progress, ct);
        var tempZipPath = bundle.Path;

        // --- Step 2: Determine output target ---
        var createNewDataset = args.CreateNewDataset ?? false;
        var targetDdb = ctx.Ddb;
        var targetDataset = ctx.Dataset;
        var targetRoot = ctx.Ddb.DatasetFolderPath;
        var targetOrgSlug = request.OrgSlug;
        var targetDsSlug = request.DsSlug;
        var destPath = _paths.NormalizeFolder(args.DestPath);

        if (createNewDataset)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new HeavyToolProgress(0.96, "creating-dataset",
                LogChunk: "Creating new dataset for photogrammetry results"));

            var newDatasetName = string.IsNullOrWhiteSpace(args.NewDatasetName)
                ? $"photogrammetry-{DateTime.UtcNow:yyyyMMddHHmmss}"
                : args.NewDatasetName;
            var newDatasetOrgSlug = args.NewDatasetOrgSlug ?? request.OrgSlug;
            var visibilityStr = args.NewDatasetVisibility ?? "PRIVATE";
            var visibility = Enum.Parse<Visibility>(visibilityStr, true);

            targetOrgSlug = newDatasetOrgSlug;

            // This tool is a singleton; resolve the scoped services from a child scope at the point of use. The
            // worker has no caller: the dataset is created by the system path, for the user who requested the task,
            // whose access is checked again here (D-031). Nothing was created before this point
            using (var scope = _scopeFactory.CreateScope())
            {
                var creation = scope.ServiceProvider.GetRequiredService<DatasetCreation>();

                // The requester may have lost the write access to the organization during the run: the task fails and
                // creates nothing. Checked with the rules of the API for that user, also when the dataset exists
                // already (a re-run writes into it on the user's behalf)
                await EnsureRequesterMayCreateAsync(scope.ServiceProvider, ctx.RequestedBy, newDatasetOrgSlug, ct);

                // A re-run of this task (a Retry, a lost run run again) finds the dataset an earlier run created in
                // the record: it imports into it again (the attempt rule deleted what that run wrote), instead of
                // finding its own slug taken, or creating a second dataset next to it
                var reused = await ReusableCreatedDatasetAsync(creation, ctx.WriteRecord, newDatasetOrgSlug, ct);

                Guid internalRef;
                if (reused is { } earlier)
                {
                    internalRef = earlier.InternalRef;
                    targetDsSlug = earlier.Slug;
                    progress.Report(new HeavyToolProgress(0.96, "creating-dataset",
                        LogChunk: $"Reusing dataset '{earlier.Slug}' in organization '{newDatasetOrgSlug}', " +
                                  "created by an earlier run of this task"));
                }
                else
                {
                    // The slug may have been taken during the run: the task fails and creates nothing
                    if (await creation.SlugIsTakenAsync(newDatasetOrgSlug, newDatasetName, ct))
                        throw new ConflictException(SlugTakenMessage(newDatasetName, newDatasetOrgSlug));

                    var created = await creation.CreateAsSystemAsync(newDatasetOrgSlug, new DatasetNewDto
                        {
                            Slug = newDatasetName,
                            Name = newDatasetName.Replace("-", " "),
                            Visibility = visibility,
                            Tagline = "Photogrammetry results"
                        },
                        inner => new BadRequestException(SlugTakenMessage(newDatasetName, newDatasetOrgSlug),
                            innerException: inner),
                        ct);

                    internalRef = created.InternalRef;
                    targetDsSlug = created.Dataset.Slug;
                    progress.Report(new HeavyToolProgress(0.96, "creating-dataset",
                        LogChunk: $"Created new dataset '{targetDsSlug}' in organization '{newDatasetOrgSlug}'"));

                    // Right after it exists: a cancelled or failed task deletes the dataset it created (FR-XC-27, D-031)
                    await ctx.WriteRecord.CreatedDatasetAsync(
                        new TaskCreatedDataset(newDatasetOrgSlug, internalRef, targetDsSlug), CancellationToken.None);
                }

                // Resolve the DDB for the new dataset.
                var ddbManager = scope.ServiceProvider.GetRequiredService<IDdbManager>();
                targetDdb = ddbManager.Get(newDatasetOrgSlug, internalRef);
                targetDataset = new DatasetKey(newDatasetOrgSlug, internalRef);
                targetRoot = targetDdb.DatasetFolderPath;
            }

            // For a new dataset, extract to root (no subfolder).
            destPath = string.Empty;
        }

        // --- Step 3: Extract archive into target dataset ---
        await ExtractArchiveIntoDataset(
            tempZipPath, destPath, targetDdb, targetDataset, targetRoot, targetOrgSlug, targetDsSlug,
            ctx, progress, ct);

        progress.Report(new HeavyToolProgress(1, "done",
            LogChunk: $"Photogrammetry complete. Results extracted to '{destPath}' in dataset '{targetDsSlug}'."));

        return null;
    }

    /// <summary>
    /// Extracts a downloaded archive into a dataset, indexes files, and enqueues build jobs.
    /// Reuses the same pattern as ArchiveExtractTool.
    /// </summary>
    private async Task ExtractArchiveIntoDataset(
        string archivePath, string destPath, IDDB ddb, DatasetKey dataset, string root,
        string orgSlug, string dsSlug,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        // The shared extractor: the path rule decides every entry, the budget every chunk, existing
        // files are kept (no overwrite for photogrammetry output), any failure leaves the dataset as it
        // was, and every result is recorded in the task's write record of the target dataset before it is
        // written (FR-XC-27)
        var writes = await TaskImportWrites.StartAsync(ctx.WriteRecord, dataset, root, ct);

        // The cap is the smaller of the archive limit and what the owner of the target organization may still store, read
        // now, when the results are written (FR-PRC-08, D-039): when they do not fit the task fails with the 507 text, and
        // the write record rolls back what was written. The internal folders of the bundle are not imported (FR-PRC-41)
        var (maxBytes, maxBytesMessage) = await WorkerStorageQuota.CapAsync(ctx.Storage, orgSlug,
            _settings.MaxArchiveExtractSizeBytes, replacedBytes: null, ct);
        var extraction = await _archives.ExtractAsync(
            new ArchiveExtractionRequest(archivePath, root, destPath, _importSettings.IsExtensionAllowed,
                target => !writes.MayWrite(target), maxBytes,
                _settings.DiskSafetyMarginBytes, writes, Exclude: ExcludedResultEntry(destPath),
                MaxBytesMessage: maxBytesMessage),
            new CallbackProgress<ArchiveExtractionProgress>(p => ReportProgress(progress, p)), ct);
        var extractedPaths = extraction.Extracted.ToList();

        ct.ThrowIfCancellationRequested();

        // Index, queue the builds of the buildable results (children of this task), invalidate the caches
        await _indexer.IndexAsync(new TaskFiles(ddb, dataset, orgSlug, dsSlug, extractedPaths, ctx.BuildQueue, ctx.TaskId,
            "photogrammetry results", ProgressFrom: 0.97, ProgressTo: 0.99), progress, ct);

        progress.Report(new HeavyToolProgress(0.99, "extracting", LogChunk: string.Join('\n', extraction.LogLines())));
    }

    // The entries of the result bundle that are not imported: the dataset path is under the folder the results go to, the
    // prefix is read relative to the bundle (FR-PRC-41)
    private Func<string, bool> ExcludedResultEntry(string destPath)
    {
        var prefixes = _settings.PhotogrammetryExcludedFoldersOrDefault();
        var root = destPath.Length == 0 ? string.Empty : destPath.TrimEnd('/') + "/";
        return target =>
        {
            var inBundle = root.Length > 0 && target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? target[root.Length..]
                : target;
            return prefixes.Any(prefix => !string.IsNullOrEmpty(prefix) &&
                                          inBundle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        };
    }

    private List<Entry> CollectImageEntries(HeavyToolRequest request, Parameters args, IDDB ddb)
    {
        var explicitImages = args.Images;
        IEnumerable<Entry> entries;

        if (explicitImages is { Count: > 0 })
        {
            entries = explicitImages
                .Select(p => ddb.GetEntry(_paths.Normalize(p)))
                .Where(e => e is not null)
                .Select(e => e!);
        }
        else
        {
            var folder = _paths.NormalizeFolder(args.Folder ?? request.Path);
            entries = ddb.Find(EntryQuery.OfTypes(folder, EntryType.Image, EntryType.GeoImage));
        }

        return
        [
            .. entries
                .Where(e => e.Type is EntryType.Image or EntryType.GeoImage)
        ];
    }

    // The extraction is the 0.96..0.98 slice of the task
    private static void ReportProgress(IProgress<HeavyToolProgress> progress, ArchiveExtractionProgress p)
    {
        if (p.Done == 0)
        {
            progress.Report(new HeavyToolProgress(p.Total > 0 ? 0.96 : -1, "extracting",
                LogChunk: p.Total > 0
                    ? $"Extracting {p.Total} file(s) from photogrammetry results"
                    : "Extracting photogrammetry results"));
            return;
        }

        var fraction = p.Total > 0 ? 0.96 + 0.02 * p.Done / p.Total : -1;
        progress.Report(new HeavyToolProgress(fraction, "extracting", Message: p.Key.Length > 0 ? p.Key : null));
    }

    // Reports inline (System.Progress<T> posts to the thread pool)
    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

}
