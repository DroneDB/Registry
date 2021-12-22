using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers
{
    public class MetaManager : IMetaManager
    {
        private readonly ILogger<MetaManager> _logger;
        private readonly IDdbManager _ddbManager;
        private readonly IAuthManager _authManager;
        private readonly IUtils _utils;

        public MetaManager(ILogger<MetaManager> logger,
            IDdbManager ddbManager, IAuthManager authManager,IUtils utils)
        {
            _logger = logger;
            _ddbManager = ddbManager;
            _utils = utils;
            _authManager = authManager;
        }

        public async Task<MetaDto> Add(string orgSlug, string dsSlug, string key, string data, string path = null)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to add meta");
            
            _logger.LogInformation("In Add('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            if (data == null)
                throw new ArgumentException("Data should not be null");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            if (path != null && !await ddb.EntryExistsAsync(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            var res = ddb.Meta.Add(key, data, path);

            return new MetaDto
            {
                Data = res.Data,
                Id = res.Id,
                ModifiedTime = res.ModifiedTime
            };
        }

        public async Task<MetaDto> Set(string orgSlug, string dsSlug, string key, string data, string path = null)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to set meta");

            _logger.LogInformation("In Set('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            if (data == null)
                throw new ArgumentException("Data should not be null");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            if (path != null && !await ddb.EntryExistsAsync(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            var res = ddb.Meta.Set(key, data, path);

            return new MetaDto
            {
                Data = res.Data,
                Id = res.Id,
                ModifiedTime = res.ModifiedTime
            };
        }

        public async Task<int> Remove(string orgSlug, string dsSlug, string id)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to remove meta");

            _logger.LogInformation("In Remove('{OrgSlug}/{DsSlug}', {Id})", orgSlug, dsSlug, id);

            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Id should not be null or empty");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            return ddb.Meta.Remove(id);
        }

        public async Task<JToken> Get(string orgSlug, string dsSlug, string key, string path = null)
        {
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In Get('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            if (path != null && !await ddb.EntryExistsAsync(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            var res = ddb.Meta.Get(key, path);

            return JsonConvert.DeserializeObject<JToken>(res);

        }

        public async Task<int> Unset(string orgSlug, string dsSlug, string key, string path = null)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to unset meta");

            _logger.LogInformation("In Unset('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            if (path != null && !await ddb.EntryExistsAsync(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            return ddb.Meta.Unset(key, path);
            
        }

        public async Task<MetaListItemDto[]> List(string orgSlug, string dsSlug, string path = null)
        {
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("In List('{OrgSlug}/{DsSlug}', {Path})", orgSlug, dsSlug, path);

            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            if (path != null && !await ddb.EntryExistsAsync(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            var res = ddb.Meta.List(path);

            return res.Select(item => new MetaListItemDto
            {
                Count = item.Count,
                Key = item.Key,
                Path = item.Path
            }).ToArray();
        }
    }
}