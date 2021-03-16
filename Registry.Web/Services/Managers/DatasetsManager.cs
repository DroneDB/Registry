using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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

            var query = from ds in org.Datasets.ToArray()

                        select new DatasetDto
                        {
                            Id = ds.Id,
                            Slug = ds.Slug,
                            CreationDate = ds.CreationDate,
                            Description = ds.Description,
                            LastEdit = ds.LastEdit,
                            Name = ds.Name,
                            Meta = ds.Meta,
                            ObjectsCount = ds.ObjectsCount,
                            Size = ds.Size
                        };

            return query;
        }

        public async Task SyncDdbMeta(string orgSlug, string dsSlug)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to sync db meta");


            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

            var attrs = ddb.ChangeAttributes(null);

            ds.Meta = attrs;
            
        }

        public async Task<DatasetDto> Get(string orgSlug, string dsSlug)
        {

            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            return dataset.ToDto();
        }

        public async Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug)
        {

            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            return new[] { _utils.GetDatasetEntry(dataset) };
        }

        public async Task<DatasetDto> AddNew(string orgSlug, DatasetDto dataset)
        {

            var org = await _utils.GetOrganization(orgSlug);

            var ds = dataset.ToEntity();

            ds.LastEdit = DateTime.Now;
            ds.CreationDate = ds.LastEdit;

            if (!string.IsNullOrEmpty(dataset.Password))
                ds.PasswordHash = _passwordHasher.Hash(dataset.Password);

            if (ds.InternalRef == Guid.Empty)
                ds.InternalRef = Guid.NewGuid();

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            ds.Meta = ddb.ChangeAttributes(ds.Meta);
            
            org.Datasets.Add(ds);

            await _context.SaveChangesAsync();
            
            return ds.ToDto();

        }

        public async Task Edit(string orgSlug, string dsSlug, DatasetDto dataset)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            ds.Description = dataset.Description;
            ds.IsPublic = dataset.IsPublic;
            ds.LastEdit = DateTime.Now;
            ds.Name = dataset.Name;

            if (!string.IsNullOrEmpty(dataset.Password))
                ds.PasswordHash = _passwordHasher.Hash(dataset.Password);

            await _context.SaveChangesAsync();

        }


        public async Task Delete(string orgSlug, string dsSlug)
        {
            var org = await _utils.GetOrganization(orgSlug);

            var ds = org.Datasets.FirstOrDefault(item => item.Slug == dsSlug);

            if (ds == null)
                throw new NotFoundException("Dataset not found");

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

            var attrs = ddb.ChangeAttributes(attributes);

            ds.Meta = attrs;
            await _context.SaveChangesAsync();

            return attrs;

        }
    }
}
