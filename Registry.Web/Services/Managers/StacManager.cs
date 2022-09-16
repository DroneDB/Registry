using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

public class StacManager : IStacManager
{
    private readonly IAuthManager _authManager;
    private readonly RegistryContext _context;
    private readonly IUtils _utils;
    private readonly IDdbManager _ddbManager;
    private readonly IDistributedCache _cache;
    private readonly ILogger<StacManager> _logger;

    private const string CacheKey = "stac-catalog";
    
    // These could end up in the config
    const string CatalogTitle = "DroneDB public datasets catalog";
    private const string CatalogId = "DroneDB Catalog";
    private readonly TimeSpan Expiration = TimeSpan.FromMinutes(5);
    
    // NOTE: We could implement a more sofisticated approach to cache invalidation
    //       but for now we just invalidate the cache every 5 minutes
    //       This is not a problem since the catalog is not updated that often

    public StacManager(
        IAuthManager authManager,
        RegistryContext context,
        IUtils utils,
        IDdbManager ddbManager,
        IDistributedCache cache,
        ILogger<StacManager> logger)
    {
        _authManager = authManager;
        _context = context;
        _utils = utils;
        _ddbManager = ddbManager;
        _cache = cache;
        _logger = logger;
    }

    private StacCatalogDto _internalGetCatalog()
    {

        var stacUrl = _utils.GenerateStacUrl();

        var links = new List<StacLinkDto>
        {
            new()
            {
                Href = stacUrl,
                Relationship = "self",
                Title = CatalogTitle
            },
            new()
            {
                Href = stacUrl,
                Relationship = "root",
                Title = CatalogTitle
            }
        };
        
        links.AddRange(from dataset in _context.Datasets.Include("Organization").ToArray()
            let ddb = _ddbManager.Get(dataset.Organization.Slug, dataset.InternalRef)
            let meta = ddb.Meta.GetSafe()
            where meta.Visibility == Visibility.Public
            select new StacLinkDto
            {
                Href = _utils.GenerateDatasetStacUrl(dataset.Organization.Slug, dataset.Slug),
                Relationship = "child",
                Title = meta.Name
            });

        return new StacCatalogDto
        {
            Type = "Catalog",
            StacVersion = "1.0.0",
            Id = CatalogId,
            Description = CatalogTitle,
            Links = links,
        };
    }
    
    public async Task<StacCatalogDto> GetCatalog()
    {
        var currentUser = await _authManager.GetCurrentUser();

        if (currentUser == null)
            throw new UnauthorizedException("Invalid user");

        var cached = await _cache.GetRecordAsync<StacCatalogDto>(CacheKey);
        if (cached != null)
            return cached;
        
        var catalog = _internalGetCatalog();
        
        await _cache.SetRecordAsync(CacheKey, catalog, Expiration);
        
        return catalog;

    }

    public async Task<JToken> GetStacChild(string orgSlug, string dsSlug, string path = null)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);
            
        _logger.LogInformation("In GetStacChild('{OrgSlug}/{DsSlug}', {Path})", orgSlug, dsSlug, path);

        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("The current user is not allowed to list this dataset");
            
        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        if (path != null && !await ddb.EntryExistsAsync(path))
            throw new ArgumentException("Entry does not exist");
        
        return ddb.GetStac($"{orgSlug}/{dsSlug}", _utils.GenerateDatasetUrl(ds), 
            _utils.GenerateStacUrl(), path);

    }

}