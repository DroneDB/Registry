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
using Registry.Web.Data.Models;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers
{

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
        private const string CatalogTitle = "DroneDB public datasets catalog";
        private const string CatalogId = "DroneDB Catalog";
        private readonly TimeSpan Expiration = TimeSpan.FromMinutes(5);

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

        
        public async Task<StacCatalogDto> GetCatalog()
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

            var datasets = _context.Datasets.Include("Organization").ToArray();

            foreach (var ds in datasets)
            {

                var key = MakeCacheKey(ds);

                var item = await _cache.GetRecordAsync<StacLinkDto>(key);

                if (item != null)
                {
                    links.Add(item);
                    await _cache.RefreshAsync(key);
                    continue;
                }
                
                var ddb = _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);
                var meta = ddb.Meta.GetSafe();
                if (meta.Visibility != Visibility.Public) continue;

                item = new StacLinkDto
                {
                    Href = _utils.GenerateDatasetStacUrl(ds.Organization.Slug, ds.Slug),
                    Relationship = "child",
                    Title = meta.Name
                };
                
                await _cache.SetRecordAsync(key, item, Expiration);

                links.Add(item);
            }

            var catalog = new StacCatalogDto
            {
                Type = "Catalog",
                StacVersion = "1.0.0",
                Id = CatalogId,
                Description = CatalogTitle,
                Links = links,
            };

            return catalog;

        }

        public async Task ClearCache(Dataset ds)
        {
            _logger.LogInformation("In ClearCache('{DsSlug}')", ds.Slug);

            await _cache.RemoveAsync(MakeCacheKey(ds));
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
                _utils.GetLocalHost(), path);

        }

        private static string MakeCacheKey(Dataset ds)
        {
            return $"{CacheKey}-{ds.InternalRef}";
        }

    }
}