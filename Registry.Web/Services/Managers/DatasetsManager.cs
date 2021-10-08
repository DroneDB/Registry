using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers
{
    public class DatasetsManager : IDatasetsManager
    {
        private readonly RegistryContext _context;
        private readonly IUtils _utils;
        private readonly ILogger<DatasetsManager> _logger;
        private readonly IObjectsManager _objectsManager;
        private readonly IPasswordHasher _passwordHasher;
        private readonly IDdbManager _ddbManager;
        private readonly IAuthManager _authManager;

        // TODO: Add extensive testing

        public DatasetsManager(
            RegistryContext context,
            IUtils utils,
            ILogger<DatasetsManager> logger,
            IObjectsManager objectsManager,
            IPasswordHasher passwordHasher,
            IDdbManager ddbManager, IAuthManager authManager)
        {
            _context = context;
            _utils = utils;
            _logger = logger;
            _objectsManager = objectsManager;
            _passwordHasher = passwordHasher;
            _ddbManager = ddbManager;
            _authManager = authManager;
        }

        public async Task<IEnumerable<DatasetDto>> List(string orgSlug)
        {

            var org = await _utils.GetOrganization(orgSlug);

            var res = new List<DatasetDto>();

            foreach (var ds in org.Datasets.ToArray())
            {
                var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
                var info = await ddb.GetInfoAsync();
                var attributes = new DdbProperties(info.Properties);

                res.Add(new DatasetDto
                {
                    Id = ds.Id,
                    Slug = ds.Slug,
                    CreationDate = ds.CreationDate,
                    Description = ds.Description,
                    LastEdit = attributes.LastUpdate,
                    IsPublic = attributes.IsPublic,
                    Name = ds.Name,
                    Properties = attributes.Properties,
                    ObjectsCount = attributes.ObjectsCount,
                    Size = info.Size
                });
            }
            
            return res.ToArray();
        }

        public async Task<DatasetDto> Get(string orgSlug, string dsSlug)
        {

            var dataset = await _utils.GetDataset(orgSlug, dsSlug);
            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            return dataset.ToDto(await ddb.GetInfoAsync());
        }

        public async Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug)
        {

            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            var info = await ddb.GetInfoAsync();
            var attrs = new DdbProperties(info.Properties);
            
            var entry = new EntryDto
            {
                ModifiedTime = attrs.LastUpdate,
                Depth = 0,
                Size = info.Size,
                Path = _utils.GenerateDatasetUrl(dataset),
                Type = EntryType.DroneDb,
                Properties = info.Properties
            };


            return new[] { entry };
        }

        public async Task<DatasetDto> AddNew(string orgSlug, DatasetDto dataset)
        {

            var org = await _utils.GetOrganization(orgSlug);

            var ds = dataset.ToEntity();

            var now = DateTime.Now;

            if (!string.IsNullOrEmpty(dataset.Password))
                ds.PasswordHash = _passwordHasher.Hash(dataset.Password);

            if (ds.InternalRef == Guid.Empty)
                ds.InternalRef = Guid.NewGuid();

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            if (dataset.Properties != null) 
                await ddb.ChangeAttributesRawAsync(dataset.Properties);

            var attributes = await ddb.GetAttributesAsync();

            attributes.IsPublic = dataset.IsPublic;
            attributes.LastUpdate = now;

            ds.CreationDate = now;

            org.Datasets.Add(ds);

            await _context.SaveChangesAsync();

            return ds.ToDto(await ddb.GetInfoAsync());

        }

        public async Task Edit(string orgSlug, string dsSlug, DatasetDto dataset)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            ds.Description = dataset.Description;
            ds.Name = dataset.Name;

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            var attributes = await ddb.GetAttributesAsync();
            attributes.IsPublic = dataset.IsPublic;
            attributes.LastUpdate = DateTime.Now;

            if (!string.IsNullOrEmpty(dataset.Password))
                ds.PasswordHash = _passwordHasher.Hash(dataset.Password);

            await _context.SaveChangesAsync();

        }


        public async Task Delete(string orgSlug, string dsSlug)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to delete dataset");

            await _objectsManager.DeleteAll(orgSlug, dsSlug);

            _context.Datasets.Remove(ds);

            await _context.SaveChangesAsync();
        }

        public async Task Rename(string orgSlug, string dsSlug, string newSlug)
        {

            if (string.IsNullOrWhiteSpace(newSlug))
                throw new ArgumentException("New slug is empty");

            if (dsSlug == MagicStrings.DefaultDatasetSlug || newSlug == MagicStrings.DefaultDatasetSlug)
                throw new ArgumentException("Cannot move default dataset");

            if (!newSlug.IsValidSlug())
                throw new ArgumentException($"Invalid slug '{newSlug}'");

            if (await _utils.GetDataset(orgSlug, newSlug, true) != null)
                throw new ArgumentException($"Dataset '{newSlug}' already exists");

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to rename dataset");

            ds.Slug = newSlug;

            await _context.SaveChangesAsync();

        }

        public async Task<Dictionary<string, object>> ChangeAttributes(string orgSlug, string dsSlug, Dictionary<string, object> attributes)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to change attributes");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            var attrs = await ddb.ChangeAttributesRawAsync(attributes);

            return attrs;

        }
    }
}
