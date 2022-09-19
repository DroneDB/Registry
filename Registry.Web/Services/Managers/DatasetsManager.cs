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
        private readonly IStacManager _stacManager;
        private readonly IDdbManager _ddbManager;
        private readonly IAuthManager _authManager;

        public DatasetsManager(
            RegistryContext context,
            IUtils utils,
            ILogger<DatasetsManager> logger,
            IObjectsManager objectsManager,
            IStacManager stacManager,
            IDdbManager ddbManager, 
            IAuthManager authManager)
        {
            _context = context;
            _utils = utils;
            _logger = logger;
            _objectsManager = objectsManager;
            _stacManager = stacManager;
            _ddbManager = ddbManager;
            _authManager = authManager;
        }

        public async Task<IEnumerable<DatasetDto>> List(string orgSlug)
        {
            var org = _utils.GetOrganization(orgSlug);
            
            if (!await _authManager.RequestAccess(org, AccessType.Read))
                throw new UnauthorizedException("The current user cannot access this organization");

            var res = new List<DatasetDto>();

            foreach (var ds in org.Datasets.ToArray())
            {
                var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
                var info = await ddb.GetInfoAsync();

                res.Add(new DatasetDto
                {
                    Slug = ds.Slug,
                    CreationDate = ds.CreationDate,
                    Properties = info.Properties,
                    Size = info.Size
                });
            }

            return res.ToArray();
        }

        public async Task<DatasetDto> Get(string orgSlug, string dsSlug)
        {
            var dataset = _utils.GetDataset(orgSlug, dsSlug);
            
            if (!await _authManager.RequestAccess(dataset, AccessType.Read))
                throw new UnauthorizedException("The current user cannot access this dataset");
            
            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            return dataset.ToDto(await ddb.GetInfoAsync());
        }

        public async Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug)
        {
            var dataset = _utils.GetDataset(orgSlug, dsSlug);
            
            if (!await _authManager.RequestAccess(dataset, AccessType.Read))
                throw new UnauthorizedException("The current user cannot access this dataset");

            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            var info = await ddb.GetInfoAsync();
            info.Depth = 0;
            info.Path = _utils.GenerateDatasetUrl(dataset, true);

            return new[] { info.ToDto() };
        }

        public async Task<DatasetDto> AddNew(string orgSlug, DatasetNewDto dataset)
        {
            var org = _utils.GetOrganization(orgSlug);

            if (!await _authManager.RequestAccess(org, AccessType.Write))
                throw new UnauthorizedException("The current user cannot add datasets to this organization");
            
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
            var meta = ddb.Meta.GetSafe();

            meta.Name = dataset.Name ?? dataset.Slug;

            if (dataset.Visibility.HasValue)
                meta.Visibility = dataset.Visibility.Value;
            
            org.Datasets.Add(ds);

            await _context.SaveChangesAsync();

            return ds.ToDto(await ddb.GetInfoAsync());
        }

        public async Task Edit(string orgSlug, string dsSlug, DatasetEditDto dataset)
        {
            if (dataset == null)
                throw new BadRequestException("Dataset is null");
            
            var ds = _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.RequestAccess(ds, AccessType.Write))
                throw new UnauthorizedException("The current user cannot edit this dataset");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            var meta = ddb.Meta.GetSafe();

            if (dataset.Visibility.HasValue)
            {
                meta.Visibility = dataset.Visibility.Value;

                await _stacManager.ClearCache(ds);
            }

            if (!string.IsNullOrWhiteSpace(dataset.Name))
                meta.Name = dataset.Name;

            await _context.SaveChangesAsync();
        }


        public async Task Delete(string orgSlug, string dsSlug)
        {
            var ds = _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.RequestAccess(ds, AccessType.Delete))
                throw new UnauthorizedException("The current user cannot delete this dataset");

            try
            {
                await _objectsManager.DeleteAll(orgSlug, dsSlug);

                _context.Datasets.Remove(ds);

                await _context.SaveChangesAsync();
                
                await _stacManager.ClearCache(ds);

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

            if (_utils.GetDataset(orgSlug, newSlug, true) != null)
                throw new ArgumentException($"Dataset '{newSlug}' already exists");

            var ds = _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.RequestAccess(ds, AccessType.Write))
                throw new UnauthorizedException("The current user cannot rename this dataset");

            ds.Slug = newSlug;

            await _context.SaveChangesAsync();
        }

        [Obsolete("Use meta")]
        public async Task<Dictionary<string, object>> ChangeAttributes(string orgSlug, string dsSlug,
            AttributesDto attributes)
        {
            if (attributes == null)
                throw new BadRequestException("Attributes are null");
            
            var ds = _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.RequestAccess(ds, AccessType.Write))
                throw new UnauthorizedException("The current user is not allowed to change attributes");

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            var res = await ddb.ChangeAttributesRawAsync(new Dictionary<string, object> { { "public", attributes.IsPublic } });;

            await _stacManager.ClearCache(ds);
            
            return res;
        }

        public async Task<StampDto> GetStamp(string orgSlug, string dsSlug)
        {
            var ds = _utils.GetDataset(orgSlug, dsSlug);
            
            if (!await _authManager.RequestAccess(ds, AccessType.Read))
                throw new UnauthorizedException("The current user cannot access this dataset");

            return _ddbManager.Get(orgSlug, ds.InternalRef).GetStamp().ToDto();
        }
    }
}