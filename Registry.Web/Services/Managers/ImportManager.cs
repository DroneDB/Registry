#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports.DroneDB;
using Registry.Ports.Import;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Import;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Default <see cref="IImportManager"/>. Verifies sources cheaply, then (on create) gates quota, makes
/// an empty dataset and submits the <c>import-dataset</c> heavy task, rolling the dataset back if the
/// submission fails.
/// </summary>
public class ImportManager : IImportManager
{
    private readonly IUtils _utils;
    private readonly IAuthManager _authManager;
    private readonly IDatasetsManager _datasetsManager;
    private readonly IImportSourceFactory _factory;
    private readonly IImportCredentialProtector _protector;
    private readonly IHeavyTaskRunner _runner;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ImportSettings _settings;
    private readonly ILogger<ImportManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportManager"/> class.
    /// </summary>
    public ImportManager(
        IUtils utils,
        IAuthManager authManager,
        IDatasetsManager datasetsManager,
        IImportSourceFactory factory,
        IImportCredentialProtector protector,
        IHeavyTaskRunner runner,
        IHttpContextAccessor httpContextAccessor,
        IOptions<AppSettings> appSettings,
        ILogger<ImportManager> logger)
    {
        _utils = utils;
        _authManager = authManager;
        _datasetsManager = datasetsManager;
        _factory = factory;
        _protector = protector;
        _runner = runner;
        _httpContextAccessor = httpContextAccessor;
        _settings = appSettings.Value.Import ?? new ImportSettings();
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableSourceTypes()
        => [.. _factory.AvailableTypes.Where(IsSourceAllowed)];

    /// <inheritdoc />
    public async Task<ImportVerifyResultDto> VerifyAsync(string orgSlug, VerifyImportRequestDto request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var org = _utils.GetOrganization(orgSlug);
        if (!await _authManager.RequestAccess(org, AccessType.Write))
            throw new UnauthorizedException("The current user cannot import datasets into this organization.");

        EnsureSourceAllowed(request.SourceType);

        var source = _factory.Resolve(request.SourceType);
        var probe = await source.ProbeAsync(ToJsonElement(request.Params), ct);

        return new ImportVerifyResultDto
        {
            Reachable = probe.Reachable,
            Note = probe.Message,
            EstimatedBytes = probe.EstimatedBytes,
            FileCount = probe.FileCount,
            SuggestedName = probe.SuggestedName,
            SuggestedSlug = Slugify(probe.SuggestedName)
        };
    }

    /// <inheritdoc />
    public async Task<ImportCreateResultDto> CreateAsync(string orgSlug, CreateImportRequestDto request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var org = _utils.GetOrganization(orgSlug);
        if (!await _authManager.RequestAccess(org, AccessType.Write))
            throw new UnauthorizedException("The current user cannot import datasets into this organization.");

        EnsureSourceAllowed(request.SourceType);

        var source = _factory.Resolve(request.SourceType);

        // Verify is mandatory: re-probe so we never submit against an unreachable source and so the
        // size/quota gate runs against fresh data.
        var probe = await source.ProbeAsync(ToJsonElement(request.Params), ct);
        if (!probe.Reachable)
            throw new BadRequestException(probe.Message ?? "The import source is not reachable.");

        // Soft quota gate when a size is known (archive-url reports the compressed size, a lower bound;
        // the worker enforces the hard cap incrementally).
        if (probe.EstimatedBytes is > 0)
            await _utils.CheckCurrentUserStorage(probe.EstimatedBytes.Value);

        var budgetBytes = await ComputeRemainingBudgetAsync();

        var name = !string.IsNullOrWhiteSpace(request.Name)
            ? request.Name!
            : probe.SuggestedName ?? "Imported dataset";
        var slug = !string.IsNullOrWhiteSpace(request.Slug)
            ? request.Slug!
            : Slugify(probe.SuggestedName ?? name);
        if (string.IsNullOrWhiteSpace(slug))
            slug = "imported-" + Guid.NewGuid().ToString("N")[..8];

        // Create the empty destination dataset.
        var ds = await _datasetsManager.AddNew(orgSlug, new DatasetNewDto
        {
            Slug = slug,
            Name = name,
            Visibility = request.Visibility ?? Visibility.Private
        });

        try
        {
            var toolParams = BuildToolParams(request.SourceType, request.Params, budgetBytes);
            var user = await _authManager.GetCurrentUser();

            var submit = new HeavyTaskSubmitRequest(
                orgSlug, ds.Slug, "import-dataset", "1", null, toolParams, false,
                user?.Id, _httpContextAccessor.HttpContext?.User);

            var result = await _runner.SubmitAsync(submit, ct);

            string? url = null;
            try
            {
                url = _utils.GenerateDatasetUrl(_utils.GetDataset(orgSlug, ds.Slug));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not generate dataset URL for {Org}/{Ds}", orgSlug, ds.Slug);
            }

            return new ImportCreateResultDto
            {
                TaskId = result.TaskId,
                OrgSlug = orgSlug,
                DsSlug = ds.Slug,
                DatasetUrl = url
            };
        }
        catch (Exception ex)
        {
            // Roll back the empty dataset so a failed submission leaves nothing behind.
            _logger.LogError(ex, "Import submission failed for {Org}/{Ds}; rolling back the dataset",
                orgSlug, ds.Slug);
            try
            {
                await _datasetsManager.Delete(orgSlug, ds.Slug);
            }
            catch (Exception delEx)
            {
                _logger.LogError(delEx, "Rollback delete failed for {Org}/{Ds}", orgSlug, ds.Slug);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ImportBrowseResultDto> BrowseAsync(string orgSlug, BrowseImportRequestDto request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var org = _utils.GetOrganization(orgSlug);
        if (!await _authManager.RequestAccess(org, AccessType.Write))
            throw new UnauthorizedException("The current user cannot browse import sources for this organization.");

        EnsureSourceAllowed(request.SourceType);

        var source = _factory.Resolve(request.SourceType);
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

    private async Task<long?> ComputeRemainingBudgetAsync()
    {
        var user = await _authManager.GetCurrentUser();
        if (user is null) return null;

        var info = _utils.GetUserStorage(user);
        if (info.Total is null) return null; // unlimited

        var remaining = info.Total.Value - info.Used;
        return remaining < 0 ? 0 : remaining;
    }

    private JsonElement BuildToolParams(string sourceType, Dictionary<string, string> source, long? budgetBytes)
    {
        var sensitive = ImportSourceDefinitions.SensitiveFields.TryGetValue(sourceType, out var fields)
            ? fields
            : (IReadOnlySet<string>)new HashSet<string>();

        var inner = new JsonObject();
        foreach (var kv in source)
        {
            var value = kv.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(value) && sensitive.Contains(kv.Key))
                value = "ENC:" + _protector.Protect(value);
            inner[kv.Key] = value;
        }

        var root = new JsonObject
        {
            ["sourceType"] = sourceType,
            ["params"] = inner
        };
        if (budgetBytes is { } b)
            root["budgetBytes"] = b;

        using var doc = JsonDocument.Parse(root.ToJsonString());
        return doc.RootElement.Clone();
    }

    private void EnsureSourceAllowed(string sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
            throw new BadRequestException("A source type is required.");

        if (!_factory.AvailableTypes.Contains(sourceType, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException($"Unknown import source type '{sourceType}'.");

        if (!IsSourceAllowed(sourceType))
            throw new BadRequestException($"The import source type '{sourceType}' is disabled on this server.");
    }

    private bool IsSourceAllowed(string sourceType)
        => _settings.IsSourceTypeAllowed(sourceType);

    private static JsonElement ToJsonElement(Dictionary<string, string> dict)
    {
        var obj = new JsonObject();
        foreach (var kv in dict)
            obj[kv.Key] = kv.Value;

        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        var lastDash = false;
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) && c < 128)
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        return sb.ToString().Trim('-');
    }
}
