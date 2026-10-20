using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// IUtils implementation: organization/dataset lookup, URL generation, and storage checks.
/// </summary>
public class WebUtils : IUtils
{
    private readonly IAuthManager _authManager;
    private readonly RegistryContext _context;
    private readonly AppSettings _settings;
    private readonly IHttpContextAccessor _accessor;
    private readonly IDatasetSummaryProvider _summaries;
    private readonly IDatasetLookupCache _lookups;

    public WebUtils(IAuthManager authManager,
        RegistryContext context,
        IOptions<AppSettings> settings,
        IHttpContextAccessor accessor, IDatasetSummaryProvider summaries,
        IDatasetLookupCache lookups = null)
    {
        _authManager = authManager;
        _context = context;
        _accessor = accessor;
        _summaries = summaries;
        _settings = settings.Value;
        _lookups = lookups;
    }


    public Organization GetOrganization(string orgSlug, bool safe = false, bool withTracking = false, bool withDatasets = false)
    {
        if (string.IsNullOrWhiteSpace(orgSlug))
            throw new BadRequestException("Missing organization id");

        if (!orgSlug.IsValidSlug())
            throw new BadRequestException("Invalid organization id");

        // Eagerly loading Datasets joins (potentially) thousands of rows on every lookup;
        // most callers only need the Organization row itself. Opt in via withDatasets.
        IQueryable<Organization> query = _context.Organizations;
        if (withDatasets)
            query = query.Include(item => item.Datasets);

        // Apply AsNoTracking only if withTracking is false (default behavior for read-only operations)
        if (!withTracking)
            query = query.AsNoTracking();

        var org = query.FirstOrDefault(item => item.Slug == orgSlug);

        if (org == null)
            return safe ? null : throw new NotFoundException("Organization not found");

        return org;
    }

    public Dataset GetDataset(string orgSlug, string dsSlug, bool safe = false, bool withTracking = false)
    {
        if (string.IsNullOrWhiteSpace(dsSlug))
            throw new BadRequestException("Missing dataset id");

        if (!dsSlug.IsValidSlug())
            throw new BadRequestException("Invalid dataset id");

        if (string.IsNullOrWhiteSpace(orgSlug))
            throw new BadRequestException("Missing organization id");

        if (!orgSlug.IsValidSlug())
            throw new BadRequestException("Invalid organization id");

        Dataset Load()
        {
            IQueryable<Dataset> query = _context.Datasets.Include(item => item.Organization);

            // Apply AsNoTracking only if withTracking is false (default behavior for read-only operations)
            if (!withTracking)
                query = query.AsNoTracking();

            return query.FirstOrDefault(item => item.Slug == dsSlug && item.Organization.Slug == orgSlug);
        }

        // With the organization's members, which the access check of a member needs: a cached
        // lookup then answers it without a query
        Dataset LoadWithMembers() => _context.Datasets
            .Include(item => item.Organization)
            .ThenInclude(org => org.Users)
            .AsNoTracking()
            .FirstOrDefault(item => item.Slug == dsSlug && item.Organization.Slug == orgSlug);

        // Read-only lookups repeat on every request: reuse them (REG-05). A tracked entity is for
        // an update, so it always comes from the database.
        var dataset = withTracking || _lookups == null ? Load() : _lookups.GetOrLoad(orgSlug, dsSlug, LoadWithMembers);

        return dataset ?? (safe ? null : throw new NotFoundException($"Cannot find dataset {dsSlug} in organization {orgSlug}"));
    }

    public string GetFreeOrganizationSlug(string orgName)
    {
        if (string.IsNullOrWhiteSpace(orgName))
            throw new BadRequestException("Empty organization name");

        var slug = orgName.ToSlug();

        var res = slug;

        for (var n = 1;; n++)
        {
            var org = _context.Organizations.AsNoTracking().FirstOrDefault(item => item.Slug == res);

            if (org == null) return res;

            res = slug + "-" + n;
        }
    }

    public string GenerateDatasetUrl(Dataset dataset, bool useDdbScheme = false)
    {
        return useDdbScheme ?
            $"{GetLocalHost(true)}/{dataset.Organization.Slug}/{dataset.Slug}" :
            $"{GetLocalHost(false)}/orgs/{dataset.Organization.Slug}/ds/{dataset.Slug}";
    }

    public string GenerateStacUrl()
    {
        return $"{GetLocalHost()}/stac";
    }

    public string GetLocalHost(bool useDdbScheme = false)
    {
        bool isHttps;
        string host;

        if (!string.IsNullOrWhiteSpace(_settings.ExternalUrlOverride))
        {
            var uri = new Uri(_settings.ExternalUrlOverride);

            isHttps = uri.Scheme.Equals("https", StringComparison.InvariantCultureIgnoreCase);
            host = uri.Host;

            // Mmmm
            if (uri.Port != 443 && uri.Port != 80)
                host += $":{uri.Port}";
        }
        else
        {
            var context = _accessor.HttpContext;
            host = context?.Request.Host.ToString() ?? "localhost";
            isHttps = context?.Request.IsHttps ?? false;
        }

        var scheme = useDdbScheme ? isHttps ? "ddb" : "ddb+unsafe" : isHttps ? "https" : "http";

        return $"{scheme}://{host}";
    }

    public string GenerateDatasetStacUrl(string orgSlug, string dsSlug)
    {
        return $"{GetLocalHost()}/orgs/{orgSlug}/ds/{dsSlug}/stac";
    }

    public UserStorageInfo GetUserStorage(User user)
    {
        if (user == null)
            throw new ArgumentException("User is null", nameof(user));

        // This is pure C# magics
        var maxStorage = user.Metadata?.SafeGetValue(MagicStrings.MaxStorageKey) is long obj ? obj : (long?)null;

        // Get all the datasets that belong to the user - using projection for efficiency
        var datasets = _context.Organizations
            .AsNoTracking()
            .Where(org => org.OwnerId == user.Id)
            .SelectMany(org => org.Datasets)
            .Select(dataset => new { OrgSlug = dataset.Organization.Slug, dataset.InternalRef })
            .ToArray();

        // Sizes are cached per dataset until its index changes: no native call per upload chunk
        var size = datasets.Sum(ds => _summaries.Get(ds.OrgSlug, ds.InternalRef).Size);

        return new UserStorageInfo
        {
            // Max storage is in MB, we need bytes to stay consistent
            Total = maxStorage * 1024 * 1024,
            Used = size
        };
    }

    public async Task<UserStorageInfo> GetCurrentUserStorageLimit()
    {
        if (!_settings.EnableStorageLimiter) return null;

        // Admins do not have limits
        if (await _authManager.IsUserAdmin()) return null;

        var storageInfo = GetUserStorage(await _authManager.GetCurrentUser());
        return storageInfo.Total == null ? null : storageInfo;
    }

    public async Task CheckCurrentUserStorage(long size = 0)
    {
        var storageInfo = await GetCurrentUserStorageLimit();
        if (storageInfo == null) return;

        var currentUsage = storageInfo.Used + size;

        if (currentUsage > storageInfo.Total)
            throw new QuotaExceededException(currentUsage, storageInfo.Total);
    }

    public static string MakeThumbTempFileName(string entryHash)
    {
        return $"thumb_{entryHash}.jpg";
    }
}