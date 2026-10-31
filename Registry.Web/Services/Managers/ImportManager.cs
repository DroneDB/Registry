using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Orchestrates importing a remote dataset (another Registry instance or a downloadable archive) into a
/// newly created dataset. Verification is mandatory before an import can be created.
/// </summary>
public class ImportManager
{
    private readonly IDatasetAccessResolver _access;
    private readonly ImportSourceCatalog _sources;
    private readonly ImportQuotaGate _quota;
    private readonly ImportTargetDatasets _targets;
    private readonly CurrentUserTaskSubmitter _submitter;
    private readonly ImportCredentials _credentials;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportManager"/> class.
    /// </summary>
    public ImportManager(
        IDatasetAccessResolver access,
        ImportSourceCatalog sources,
        ImportQuotaGate quota,
        ImportTargetDatasets targets,
        CurrentUserTaskSubmitter submitter,
        ImportCredentials credentials)
    {
        _access = access;
        _sources = sources;
        _quota = quota;
        _targets = targets;
        _submitter = submitter;
        _credentials = credentials;
    }

    /// <summary>
    /// Returns the import source types enabled on this deployment (registered AND allow-listed) to a
    /// caller who may import into the organization (write access), like the other import operations.
    /// </summary>
    /// <param name="orgSlug">The destination organization slug.</param>
    /// <returns>The available source type identifiers.</returns>
    public async Task<IReadOnlyList<string>> GetAvailableSourceTypes(string orgSlug, CancellationToken ct = default)
    {
        await RequireImportAccess(orgSlug, ct);
        return _sources.AllowedTypes;
    }

    // Every import operation needs write access to the destination organization
    private async Task RequireImportAccess(string orgSlug, CancellationToken ct) =>
        await _access.ResolveOrganizationAsync(orgSlug, AccessType.Write, ct: ct);

    /// <summary>
    /// Verifies (probes) an import source: checks reachability/credentials and returns cheap metadata
    /// (estimated size, file count, a suggested name/slug) without creating anything.
    /// </summary>
    /// <param name="orgSlug">The destination organization slug.</param>
    /// <param name="request">The verify request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The probe result.</returns>
    public async Task<ImportVerifyResultDto> VerifyAsync(string orgSlug, VerifyImportRequestDto request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await RequireImportAccess(orgSlug, ct);

        EnsureSourceAllowed(request.SourceType);

        var source = _sources.Resolve(request.SourceType);
        var probe = await source.ProbeAsync(ToJsonElement(request.Params), ct);

        return new ImportVerifyResultDto
        {
            Reachable = probe.Reachable,
            Note = probe.Message,
            EstimatedBytes = probe.EstimatedBytes,
            FileCount = probe.FileCount,
            SuggestedName = probe.SuggestedName,
            SuggestedSlug = ImportDatasetNaming.Slugify(probe.SuggestedName)
        };
    }

    /// <summary>
    /// Creates an empty dataset and submits a heavy task that imports the source into it.
    /// </summary>
    /// <param name="orgSlug">The destination organization slug.</param>
    /// <param name="request">The create request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created dataset and the tracking task id.</returns>
    public async Task<ImportCreateResultDto> CreateAsync(string orgSlug, CreateImportRequestDto request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await RequireImportAccess(orgSlug, ct);

        EnsureSourceAllowed(request.SourceType);

        var source = _sources.Resolve(request.SourceType);

        // Verify is mandatory: re-probe so we never submit against an unreachable source and so the
        // size/quota gate runs against fresh data.
        var probe = await source.ProbeAsync(ToJsonElement(request.Params), ct);
        if (!probe.Reachable)
            throw new BadRequestException(probe.Message ?? "The import source is not reachable.");

        // Soft quota gate when a size is known (archive-url reports the compressed size, a lower bound;
        // the worker enforces the hard cap incrementally).
        if (probe.EstimatedBytes is > 0)
            await _quota.CheckAsync(probe.EstimatedBytes.Value);

        var budgetBytes = await _quota.RemainingBytesAsync();

        var name = ImportDatasetNaming.NameOf(request.Name, probe);
        var slug = ImportDatasetNaming.SlugOf(request.Slug, probe, name);

        // Create the empty destination dataset.
        var ds = await _targets.CreateAsync(orgSlug, slug, name, request.Visibility ?? Visibility.Private, ct);

        try
        {
            var toolParams = BuildToolParams(request.SourceType, request.Params, budgetBytes);

            var result = await _submitter.SubmitAsync(orgSlug, ds.Slug, "import-dataset", "1", null, toolParams,
                force: false, ct);

            return new ImportCreateResultDto
            {
                TaskId = result.TaskId,
                OrgSlug = orgSlug,
                DsSlug = ds.Slug,
                DatasetUrl = await _targets.UrlOfAsync(orgSlug, ds.Slug, ct)
            };
        }
        catch (Exception ex)
        {
            // Roll back the empty dataset so a failed submission leaves nothing behind.
            await _targets.RollBackAsync(orgSlug, ds.Slug, ex);
            throw;
        }
    }

    /// <summary>
    /// Browses the remote structure of an import source (lists organizations or datasets) without
    /// creating or modifying anything. Only sources that implement
    /// <see cref="Registry.Web.Services.Import.IBrowsableImportSource"/> support this operation.
    /// </summary>
    /// <param name="orgSlug">The destination organization slug (used for access-control only).</param>
    /// <param name="request">The browse request specifying source type, params and browse type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The browse result containing the matching items.</returns>
    public async Task<ImportBrowseResultDto> BrowseAsync(string orgSlug, BrowseImportRequestDto request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await RequireImportAccess(orgSlug, ct);

        EnsureSourceAllowed(request.SourceType);

        var source = _sources.Resolve(request.SourceType);
        if (source is not IBrowsableImportSource browsable)
            throw new BadRequestException($"The '{request.SourceType}' source does not support browsing.");

        var paramsElement = ToJsonElement(request.Params);
        var items = request.BrowseType switch
        {
            "organizations" => await browsable.BrowseOrganizationsAsync(paramsElement, ct),
            "datasets" => await browsable.BrowseDatasetsAsync(paramsElement, ct),
            _ => throw new BadRequestException($"Unknown browse type '{request.BrowseType}'. Use 'organizations' or 'datasets'.")
        };

        return new ImportBrowseResultDto
        {
            Items =
            [
                .. items
                    .Select(i => new BrowseItemDto { Slug = i.Slug, Name = i.Name })
            ]
        };
    }

    private JsonElement BuildToolParams(string sourceType, Dictionary<string, string> source, long? budgetBytes)
    {
        var root = new JsonObject
        {
            ["sourceType"] = sourceType,
            ["params"] = _credentials.SealParameters(sourceType, source)
        };
        if (budgetBytes is { } b)
            root["budgetBytes"] = b;

        return root.ToElement();
    }

    private void EnsureSourceAllowed(string sourceType)
    {
        if (_sources.Refusal(sourceType) is { } refusal)
            throw new BadRequestException(refusal);
    }

    private static JsonElement ToJsonElement(Dictionary<string, string> dict)
    {
        var obj = new JsonObject();
        foreach (var kv in dict)
            obj[kv.Key] = kv.Value;

        return obj.ToElement();
    }
}
