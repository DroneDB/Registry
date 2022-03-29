using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB.Models;
using Registry.Web.Data;
using Registry.Web.Data.Models;
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
        private readonly IDdbManager _ddbManager;
        private readonly IAuthManager _authManager;

        public DatasetsManager(
            RegistryContext context,
            IUtils utils,
            ILogger<DatasetsManager> logger,
            IObjectsManager objectsManager,
            IDdbManager ddbManager, IAuthManager authManager)
        {
            _context = context;
            _utils = utils;
            _logger = logger;
            _objectsManager = objectsManager;
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
                var attributes = new EntryProperties(info.Properties);

                res.Add(new DatasetDto
                {
                    Slug = ds.Slug,
                    CreationDate = ds.CreationDate,
                    Properties = attributes.Properties,
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
            info.Depth = 0;
            info.Path = _utils.GenerateDatasetUrl(dataset);

            return new[] { info.ToDto() };
        }

        public async Task<DatasetDto> AddNew(string orgSlug, DatasetNewDto dataset)
        {
            var org = await _utils.GetOrganization(orgSlug);

            if (!await _authManager.IsOwnerOrAdmin(org)) 
                throw new UnauthorizedException("You are not authorized to add datasets to this organization");
            
            if (dataset == null)
                throw new BadRequestException("Dataset is null");
            
            if (dataset.Slug == null)
                throw new BadRequestException("Dataset slug is null");

            if (!dataset.Slug.IsValidSlug())
                throw new BadRequestException("Dataset slug is invalid");
            
            if (_context.Datasets.Any(item => item.Slug == dataset.Slug && item.Organization.Slug == orgSlug))
                throw new BadRequestException("Dataset with this slug already exists");
            
            var ds = new Dataset
            {
                Slug = dataset.Slug,
                Organization = org,
                InternalRef = Guid.NewGuid(),
                CreationDate = DateTime.Now
            };

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            
            ddb.Meta.GetSafe().Name = dataset.Name ?? dataset.Slug;

            var attributes = await ddb.GetAttributesAsync();

            if (dataset.IsPublic.HasValue)
                attributes.IsPublic = dataset.IsPublic.Value;

            org.Datasets.Add(ds);

            await _context.SaveChangesAsync();

            return ds.ToDto(await ddb.GetInfoAsync());
        }

        public async Task Edit(string orgSlug, string dsSlug, DatasetEditDto dataset)
        {
            if (dataset == null)
                throw new BadRequestException("Dataset is null");
            
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            var attributes = await ddb.GetAttributesAsync();

            if (dataset.IsPublic != null)
                attributes.IsPublic = dataset.IsPublic.Value;

            if (!string.IsNullOrWhiteSpace(dataset.Name))
                ddb.Meta.GetSafe().Name = dataset.Name;

            await _context.SaveChangesAsync();
        }


        public async Task Delete(string orgSlug, string dsSlug)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to delete dataset");

            try
            {
                await _objectsManager.DeleteAll(orgSlug, dsSlug);

                _context.Datasets.Remove(ds);

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error deleting dataset", ex);
                throw new InvalidOperationException("Error deleting dataset", ex);
            }
        }

        public async Task Rename(string orgSlug, string dsSlug, string newSlug)
        {
            if (string.IsNullOrWhiteSpace(newSlug))
                throw new ArgumentException("New slug is empty");

            if (dsSlug == MagicStrings.DefaultDatasetSlug || newSlug == MagicStrings.DefaultDatasetSlug)
                throw new ArgumentException("Cannot move default dataset");

            if (!newSlug.IsValidSlug())
                throw new ArgumentException($"Invalid slug '{newSlug}'");

            if (dsSlug == newSlug) return; // Nothing to do

            if (await _utils.GetDataset(orgSlug, newSlug, true) != null)
                throw new ArgumentException($"Dataset '{newSlug}' already exists");

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to rename dataset");

            ds.Slug = newSlug;

            await _context.SaveChangesAsync();
        }

        public async Task<Dictionary<string, object>> ChangeAttributes(string orgSlug, string dsSlug,
            AttributesDto attributes)
        {
            
            if (attributes == null)
                throw new BadRequestException("Attributes are null");
            
            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to change attributes");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            var attrs = await ddb.GetAttributesAsync();
            attrs.IsPublic = attributes.IsPublic;

            return await ddb.GetAttributesRawAsync();
        }

        public async Task<StampDto> GetStamp(string orgSlug, string dsSlug)
        {
            var ds = await _utils.GetDataset(orgSlug, dsSlug);
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            return ddb.GetStamp().ToDto();
        }
    }
}