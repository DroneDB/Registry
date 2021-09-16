using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers
{
    public class MetaManager : IMetaManager
    {
        private readonly ILogger<MetaManager> _logger;
        private readonly IDdbManager _ddbManager;
        private readonly IUtils _utils;

        public MetaManager(ILogger<MetaManager> logger,
            IDdbManager ddbManager,
            IUtils utils)
        {
            _logger = logger;
            _ddbManager = ddbManager;
            _utils = utils;
        }

        public async Task<MetaDto> Add(string orgSlug, string dsSlug, string key, JObject data, string path = null)
        {
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Add('{orgSlug}/{dsSlug}', {key}, {path})");

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            if (data == null)
                throw new ArgumentException("Data should not be null");

            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            if (path != null && !ddb.EntryExists(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            var res = ddb.Meta.Add(key, data, path);

            return new MetaDto
            {
                Data = res.Data,
                Id = res.Id,
                ModifiedTime = res.ModifiedTime
            };
        }

        public async Task<MetaDto> Set(string orgSlug, string dsSlug, string key, JObject data, string path = null)
        {
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Set('{orgSlug}/{dsSlug}', {key}, {path})");

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            if (data == null)
                throw new ArgumentException("Data should not be null");

            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            if (path != null && !ddb.EntryExists(path))
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
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Remove('{orgSlug}/{dsSlug}', {id})");

            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Id should not be null or empty");

            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            return ddb.Meta.Remove(id);
        }

        public async Task<MetaDto[]> Get(string orgSlug, string dsSlug, string key, string path = null)
        {
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Get('{orgSlug}/{dsSlug}', {key}, {path})");

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            if (path != null && !ddb.EntryExists(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            var res = ddb.Meta.Get(key, path);

            return res.Select(item => new MetaDto
            {
                Data = item.Data,
                Id = item.Id,
                ModifiedTime = item.ModifiedTime
            }).ToArray();

        }

        public async Task<int> Unset(string orgSlug, string dsSlug, string key, string path = null)
        {
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In Unset('{orgSlug}/{dsSlug}', {key}, {path})");

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key should not be null or empty");

            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            if (path != null && !ddb.EntryExists(path))
                throw new ArgumentException($"Path '{path}' does not exist");

            return ddb.Meta.Unset(key, path);
            
        }

        public async Task<MetaListItemDto[]> List(string orgSlug, string dsSlug, string path = null)
        {
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation($"In List('{orgSlug}/{dsSlug}', {path})");

            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            if (path != null && !ddb.EntryExists(path))
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