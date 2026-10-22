#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Import;
using Registry.Web.Utilities;

namespace Registry.Web.Services.RemoteImport;

/// <summary>
/// Imports a dataset, or every dataset of an organization, from another Registry (RQ-12, out of
/// <c>SystemManager</c>): authenticates when credentials are given, lists the datasets of an organization, and
/// hands each one to the <see cref="IRemoteImportStrategy"/> of the requested mode. What fails is reported in the
/// result; the other datasets go on. Cancellation (the job deleted, the host stopping) stops the import.
/// </summary>
public sealed class RemoteRegistryImporter
{
    private readonly IRemoteRegistryClient _remote;
    private readonly Dictionary<ImportMode, IRemoteImportStrategy> _strategies;
    private readonly TimeProvider _time;
    private readonly ILogger<RemoteRegistryImporter> _logger;

    public RemoteRegistryImporter(IRemoteRegistryClient remote, IEnumerable<IRemoteImportStrategy> strategies,
        TimeProvider time, ILogger<RemoteRegistryImporter> logger)
    {
        _remote = remote;
        _strategies = strategies.ToDictionary(s => s.Mode);
        _time = time;
        _logger = logger;
    }

    /// <param name="request">What to import (validated when the job was queued).</param>
    /// <param name="password">The source password, decrypted; null for an anonymous import.</param>
    /// <param name="ct">Cancels the import.</param>
    public async Task<ImportResultDto> ImportAsync(RemoteImportRequest request, string? password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = _time.GetTimestamp();
        var report = new ImportReport(_time);
        var strategy = _strategies.TryGetValue(request.Mode, out var s)
            ? s
            : throw new ArgumentException($"No import strategy for mode {request.Mode}", nameof(request));

        try
        {
            await ImportDatasetsAsync(request, password, strategy, report, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Cannot import {Org}/{Ds} from {RegistryUrl}", request.SourceOrganization,
                request.SourceDataset ?? "*", request.RegistryUrl);
            report.Error(request.SourceOrganization, request.SourceDataset, ex.Message, ImportPhase.General);
        }

        return report.ToResult(_time.GetElapsedTime(started));
    }

    private async Task ImportDatasetsAsync(RemoteImportRequest request, string? password,
        IRemoteImportStrategy strategy, ImportReport report, CancellationToken ct)
    {
        string? token = null;
        if (!string.IsNullOrWhiteSpace(request.Username) && !string.IsNullOrWhiteSpace(password))
        {
            token = await _remote.AuthenticateAsync(request.RegistryUrl, request.Username, password, ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                report.Error(request.SourceOrganization, request.SourceDataset, "Authentication failed",
                    ImportPhase.Authentication);
                return;
            }
        }
        else
            _logger.LogInformation("No credentials: anonymous import from {RegistryUrl}", request.RegistryUrl);

        foreach (var (sourceDs, destinationDs) in await DatasetsAsync(request, token, ct))
        {
            var dataset = new RemoteDataset(request.RegistryUrl, token, request.SourceOrganization, sourceDs,
                request.DestinationOrganization, destinationDs);
            await ImportDatasetAsync(dataset, strategy, report, ct);
        }
    }

    // The datasets to import and the slug each one gets
    private async Task<IEnumerable<(string Source, string Destination)>> DatasetsAsync(RemoteImportRequest request,
        string? token, CancellationToken ct)
    {
        if (request.SourceDataset != null)
            return [(request.SourceDataset, request.DestinationDataset ?? request.SourceDataset)];

        var datasets = await _remote.ListDatasetsAsync(request.RegistryUrl, token, request.SourceOrganization, ct);
        _logger.LogInformation("Found {Count} datasets in {Org} on {RegistryUrl}", datasets.Length,
            request.SourceOrganization, request.RegistryUrl);
        return datasets.Select(d => (d.Slug, d.Slug));
    }

    private async Task ImportDatasetAsync(RemoteDataset dataset, IRemoteImportStrategy strategy, ImportReport report,
        CancellationToken ct)
    {
        // An import creates organizations and datasets: the slug rules of the Hub apply (a source may still hold
        // legacy slugs like "a--b")
        if (!dataset.DestinationOrganization.IsValidSlug() || !dataset.DestinationDataset.IsValidSlug())
        {
            report.Error(dataset, $"Invalid destination slug '{dataset.DestinationOrganization}/" +
                                  $"{dataset.DestinationDataset}': choose a valid destination", ImportPhase.General);
            return;
        }

        try
        {
            await strategy.ImportAsync(dataset, report, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Cannot import {Org}/{Ds}", dataset.SourceOrganization, dataset.SourceDataset);
            report.Error(dataset, ex.Message, ImportPhase.General);
        }
    }
}
