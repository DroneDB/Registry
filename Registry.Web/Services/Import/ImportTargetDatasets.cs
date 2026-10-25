#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Import;

/// <summary>The empty dataset a dataset import fills (out of the import manager).</summary>
public sealed class ImportTargetDatasets
{
    private readonly IDatasetsManager _datasetsManager;
    private readonly IDatasetResolver _datasets;
    private readonly IUrlBuilder _urls;
    private readonly ILogger<ImportTargetDatasets> _logger;

    public ImportTargetDatasets(IDatasetsManager datasetsManager, IDatasetResolver datasets, IUrlBuilder urls,
        ILogger<ImportTargetDatasets> logger)
    {
        _datasetsManager = datasetsManager;
        _datasets = datasets;
        _urls = urls;
        _logger = logger;
    }

    /// <summary>Creates the empty destination dataset.</summary>
    public Task<DatasetDto> CreateAsync(string orgSlug, string slug, string name, Visibility visibility,
        CancellationToken ct) =>
        _datasetsManager.AddNew(orgSlug, new DatasetNewDto { Slug = slug, Name = name, Visibility = visibility }, ct);

    /// <summary>The URL of the dataset, or null when it cannot be built (logged: the import was queued anyway).</summary>
    public async Task<string?> UrlOfAsync(string orgSlug, string dsSlug, CancellationToken ct)
    {
        try
        {
            return _urls.GenerateDatasetUrl(await _datasets.GetDatasetAsync(orgSlug, dsSlug, ct: ct));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not generate dataset URL for {Org}/{Ds}", orgSlug, dsSlug);
            return null;
        }
    }

    /// <summary>Deletes the dataset after a failed submission, so that nothing is left behind (a failed delete is logged).</summary>
    public async Task RollBackAsync(string orgSlug, string dsSlug, Exception cause)
    {
        _logger.LogError(cause, "Import submission failed for {Org}/{Ds}; rolling back the dataset", orgSlug, dsSlug);
        try
        {
            await _datasetsManager.Delete(orgSlug, dsSlug, CancellationToken.None);
        }
        catch (Exception delEx)
        {
            _logger.LogError(delEx, "Rollback delete failed for {Org}/{Ds}", orgSlug, dsSlug);
        }
    }
}
