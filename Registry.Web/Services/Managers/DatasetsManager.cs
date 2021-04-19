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
                        let ddb = _ddbManager.Get(orgSlug, ds.InternalRef)
                        let attributes = ddb.GetAttributes()
                        select new DatasetDto
                        {
                            Id = ds.Id,
                            Slug = ds.Slug,
                            CreationDate = ds.CreationDate,
                            Description = ds.Description,
                            LastEdit = attributes.LastUpdate,
                            IsPublic = attributes.IsPublic,
                            Name = ds.Name,
                            Meta = attributes.Meta,
                            ObjectsCount = ds.ObjectsCount,
                            Size = ds.Size
                        };

            return query;
        }

        public async Task<DatasetDto> Get(string orgSlug, string dsSlug)
        {

            var dataset = await _utils.GetDataset(orgSlug, dsSlug);
            var ddbManager = _ddbManager.Get(orgSlug, dataset.InternalRef);

            return dataset.ToDto(ddbManager.GetAttributes());
        }

        public async Task<EntryDto[]> GetEntry(string orgSlug, string dsSlug)
        {

            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            var ddb = _ddbManager.Get(orgSlug, dataset.InternalRef);

            return new[] { _utils.GetDatasetEntry(dataset, ddb.GetAttributes()) };
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

            if (dataset.Meta != null) 
                ddb.ChangeAttributesRaw(dataset.Meta);

            var attributes = ddb.GetAttributes();
            attributes.IsPublic = dataset.IsPublic;

            attributes.LastUpdate = now;
            ds.CreationDate = now;

            org.Datasets.Add(ds);

            await _context.SaveChangesAsync();

            return ds.ToDto(attributes);

        }

        public async Task Edit(string orgSlug, string dsSlug, DatasetDto dataset)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            if (!await _authManager.IsOwnerOrAdmin(ds))
                throw new UnauthorizedException("The current user is not allowed to edit dataset");

            ds.Description = dataset.Description;
            ds.Name = dataset.Name;

            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            var attributes = ddb.GetAttributes();
            attributes.IsPublic = dataset.IsPublic;
            attributes.LastUpdate = DateTime.Now;

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
            var attrs = ddb.ChangeAttributesRaw(attributes);

            return attrs;

        }
    }
}
