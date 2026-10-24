#nullable enable
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
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Default <see cref="IImportManager"/>. Verifies sources cheaply, then (on create) gates quota, makes
/// an empty dataset and submits the <c>import-dataset</c> heavy task, rolling the dataset back if the
/// submission fails. The pieces are the services RQ-12 split it into (12 parameters before): the sources
/// a server offers, the quota gate, the target dataset and the credentials of the task parameters.
/// </summary>
public class ImportManager : IImportManager
{
    private readonly IDatasetAccessResolver _access;
    private readonly IImportSourceCatalog _sources;
    private readonly IImportQuotaGate _quota;
    private readonly IImportTargetDatasets _targets;
    private readonly ICurrentUserTaskSubmitter _submitter;
    private readonly ImportCredentials _credentials;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportManager"/> class.
    /// </summary>
    public ImportManager(
        IDatasetAccessResolver access,
        IImportSourceCatalog sources,
        IImportQuotaGate quota,
        IImportTargetDatasets targets,
        ICurrentUserTaskSubmitter submitter,
        ImportCredentials credentials)
    {
        _access = access;
        _sources = sources;
        _quota = quota;
        _targets = targets;
        _submitter = submitter;
        _credentials = credentials;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetAvailableSourceTypes(string orgSlug, CancellationToken ct = default)
    {
        await RequireImportAccess(orgSlug, ct);
        return _sources.AllowedTypes;
    }

    // Every import operation needs write access to the destination organization
    private async Task RequireImportAccess(string orgSlug, CancellationToken ct) =>
        await _access.ResolveOrganizationAsync(orgSlug, AccessType.Write, ct: ct);

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
