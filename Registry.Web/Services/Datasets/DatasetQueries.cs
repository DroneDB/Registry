using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Datasets;

/// <summary>The datasets as their readers see them: the readable datasets of an organization, one dataset, its root entry, its stamp.</summary>
public interface IDatasetQueries
{
    Task<IEnumerable<DatasetDto>> List(string orgSlug, bool includeThumbnailCheck = false, CancellationToken ct = default);
    Task<(IReadOnlyList<DatasetDto> Items, int Total)> ListPage(string orgSlug, DatasetListQuery query,
        bool includeThumbnailCheck = false, CancellationToken ct = default);
    Task<DatasetDto> GetDataset(string orgSlug, string dsSlug, CancellationToken ct = default);
    Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug, CancellationToken ct = default);
    Task<StampDto> GetStamp(string orgSlug, string dsSlug, CancellationToken ct = default);
}

/// <summary>Default <see cref="IDatasetQueries"/> (RQ-12, out of <c>DatasetsManager</c>).</summary>
public sealed class DatasetQueries : IDatasetQueries
{
    private readonly IDatasetAccessResolver _access;
    private readonly IAuthManager _authManager;
    private readonly RegistryContext _context;
    private readonly IDatasetSummaryProvider _summaries;
    private readonly IDdbManager _ddbManager;
    private readonly IUrlBuilder _urls;
    private readonly ThumbnailOptions _thumbnails;

    public DatasetQueries(IDatasetAccessResolver access, IAuthManager authManager, RegistryContext context, IDatasetSummaryProvider summaries, IDdbManager ddbManager, IUrlBuilder urls, IOptions<ThumbnailOptions> thumbnails)
    {
        _access = access;
        _authManager = authManager;
        _context = context;
        _summaries = summaries;
        _ddbManager = ddbManager;
        _urls = urls;
        _thumbnails = thumbnails.Value;
    }

    public async Task<IEnumerable<DatasetDto>> List(string orgSlug, bool includeThumbnailCheck = false, CancellationToken ct = default) =>
        (await ListPage(orgSlug, new DatasetListQuery(), includeThumbnailCheck, ct: ct)).Items;

    public async Task<(IReadOnlyList<DatasetDto> Items, int Total)> ListPage(string orgSlug, DatasetListQuery query,
        bool includeThumbnailCheck = false, CancellationToken ct = default)
    {
        var org = await _access.ResolveOrganizationAsync(orgSlug, AccessType.Read, withDatasets: true, ct: ct);

        var datasets = org.Datasets.ToArray();

        // Optimization C: Short-circuit for owner/admin - they can read all datasets
        var isOwnerOrAdmin = await _authManager.IsOwnerOrAdmin(org);

        // Optimization D: Prefetch Organization.Users once to avoid N lazy loads
        if (!isOwnerOrAdmin && org.Users == null)
            await _context.Entry(org).Collection(o => o.Users).LoadAsync(ct);

        var readable = new List<(DatasetDto Dto, Dataset Dataset)>();

        foreach (var ds in datasets)
        {
            DatasetPermissionsDto permissions;

            if (isOwnerOrAdmin)
            {
                // Owner/admin has full access - skip permission check
                permissions = new DatasetPermissionsDto
                {
                    CanRead = true,
                    CanWrite = true,
                    CanDelete = true
                };
            }
            else
            {
                // Check if user can read this dataset before including it
                permissions = await _authManager.GetDatasetPermissions(ds);
                if (!permissions.CanRead)
                    continue;
            }

            // Sizes and properties are cached per dataset until its index changes (REG-06)
            var summary = _summaries.Get(orgSlug, ds.InternalRef);

            readable.Add((new DatasetDto
            {
                Slug = ds.Slug,
                CreationDate = ds.CreationDate,
                Properties = summary.Properties,
                Size = summary.Size,
                Permissions = permissions
            }, ds));
        }

        var bySlug = readable.ToDictionary(r => r.Dto.Slug, r => r.Dataset);
        var matching = query.Apply(readable.Select(r => r.Dto)).ToList();
        var total = string.IsNullOrWhiteSpace(query.Query)
            ? readable.Count
            : (query with { Skip = 0, Take = null }).Apply(readable.Select(r => r.Dto)).Count();

        // The only per-dataset native call left: for the page shown, not the whole organization
        if (includeThumbnailCheck)
            foreach (var dto in matching)
                dto.HasThumbnail = CheckHasThumbnail(_ddbManager.Get(orgSlug, bySlug[dto.Slug].InternalRef));

        return (matching, total);
    }

    public async Task<DatasetDto> GetDataset(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        var dataset = access.Dataset;

        var ddb = access.Ddb;

        var dto = dataset.ToDto(ddb.GetInfo());
        dto.Permissions = await _authManager.GetDatasetPermissions(dataset);
        dto.HasThumbnail = CheckHasThumbnail(ddb);

        return dto;
    }

    private bool CheckHasThumbnail(IDDB ddb)
    {
        var rootEntries = ddb.FindSummaries(new EntryQuery(Recursive: false));
        var rootNames = new HashSet<string>(
            rootEntries.Select(e => Path.GetFileName(e.Path)),
            StringComparer.OrdinalIgnoreCase);
        return _thumbnails.Candidates.Any(c => rootNames.Contains(c));
    }

    public async Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        var dataset = access.Dataset;

        var ddb = access.Ddb;

        var info = ddb.GetInfo();
        info.Depth = 0;
        info.Path = _urls.GenerateDatasetUrl(dataset, true);

        var dto = info.ToDto();

        // Add permissions to properties
        if (dto.Properties == null)
            dto.Properties = new Dictionary<string, object>();

        var permissions = await _authManager.GetDatasetPermissions(dataset);
        dto.Properties["permissions"] = new
        {
            canRead = permissions.CanRead,
            canWrite = permissions.CanWrite,
            canDelete = permissions.CanDelete
        };

        return [dto];
    }

    public async Task<StampDto> GetStamp(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);

        return access.Ddb.GetStamp().ToDto();
    }
}
