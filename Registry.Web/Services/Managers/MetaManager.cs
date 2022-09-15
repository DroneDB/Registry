using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

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
            var ds = _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.RequestAccess(ds, AccessType.Write))
                throw new UnauthorizedException("The current user is not allowed to write to this dataset");
            
            _logger.LogInformation("In Add('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            if (data == null)
                throw new ArgumentException("Data should not be null");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            if (path != null && !await ddb.EntryExistsAsync(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            return ddb.Meta.Add(key, data, path).ToDto();
        }

        public async Task<MetaDto> Set(string orgSlug, string dsSlug, string key, string data, string path = null)
        {
            var ds = _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.RequestAccess(ds, AccessType.Write))
                throw new UnauthorizedException("The current user is not allowed to write to this dataset");

            _logger.LogInformation("In Set('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            if (data == null)
                throw new ArgumentException("Data should not be null");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            if (path != null && !await ddb.EntryExistsAsync(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            return ddb.Meta.Set(key, data, path).ToDto();
        }

        public async Task<int> Remove(string orgSlug, string dsSlug, string id)
        {
            var ds = _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.RequestAccess(ds, AccessType.Write))
                throw new UnauthorizedException("The current user is not allowed to remove meta");

            _logger.LogInformation("In Remove('{OrgSlug}/{DsSlug}', {Id})", orgSlug, dsSlug, id);

            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Id should not be null or empty");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            return ddb.Meta.Remove(id);
        }

        public async Task<JToken> Get(string orgSlug, string dsSlug, string key, string path = null)
        {
            var ds = _utils.GetDataset(orgSlug, dsSlug);
            
            if (!await _authManager.RequestAccess(ds, AccessType.Read))
                throw new UnauthorizedException("The current user is not allowed to read meta");

            _logger.LogInformation("In Get('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            if (path != null && !await ddb.EntryExistsAsync(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            return ddb.Meta.Get(key, path);

        }

        public async Task<int> Unset(string orgSlug, string dsSlug, string key, string path = null)
        {
            var ds = _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.RequestAccess(ds, AccessType.Write))
                throw new UnauthorizedException("The current user is not allowed to unset meta");

            _logger.LogInformation("In Unset('{OrgSlug}/{DsSlug}', {Key}, {Path})", orgSlug, dsSlug, key, path);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            if (path != null && !await ddb.EntryExistsAsync(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            return ddb.Meta.Unset(key, path);
            
        }

        public async Task<IEnumerable<MetaListItemDto>> List(string orgSlug, string dsSlug, string path = null)
        {
            var ds = _utils.GetDataset(orgSlug, dsSlug);
            
            if (!await _authManager.RequestAccess(ds, AccessType.Read))
                throw new UnauthorizedException("The current user is not allowed to list meta");

            _logger.LogInformation("In List('{OrgSlug}/{DsSlug}', {Path})", orgSlug, dsSlug, path);

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            if (path != null && !await ddb.EntryExistsAsync(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            return ddb.Meta.List(path).Select(item => item.ToDto());
        }

        public async Task<IEnumerable<MetaDumpDto>> Dump(string orgSlug, string dsSlug, string ids = null)
        {
            var ds = _utils.GetDataset(orgSlug, dsSlug);
            
            if (!await _authManager.RequestAccess(ds, AccessType.Read))
                throw new UnauthorizedException("The current user is not allowed to dump meta");
            
            _logger.LogInformation("In Dump('{OrgSlug}/{DsSlug}', {Ids})", orgSlug, dsSlug, ids);
            
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            return ddb.Meta.Dump(ids).Select(item => item.ToDto());
        }
    }
}