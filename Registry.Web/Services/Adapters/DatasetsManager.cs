using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters
{
    public class DatasetsManager : IDatasetsManager
    {
        private readonly RegistryContext _context;
        private readonly IUtils _utils;
        private readonly ILogger<DatasetsManager> _logger;
        private readonly IObjectsManager _objectsManager;
        private readonly IPasswordHasher _passwordHasher;
        private readonly IDdbManager _ddbManager;

        // TODO: Add extensive testing

        public DatasetsManager(
            RegistryContext context,
            IUtils utils,
            ILogger<DatasetsManager> logger,
            IObjectsManager objectsManager,
            IPasswordHasher passwordHasher,
            IDdbManager ddbManager)
        {
            _context = context;
            _utils = utils;
            _logger = logger;
            _objectsManager = objectsManager;
            _passwordHasher = passwordHasher;
            _ddbManager = ddbManager;
        }

        public async Task<IEnumerable<DatasetDto>> List(string orgSlug)
        {
            var org = await _utils.GetOrganization(orgSlug);

            var query = from ds in org.Datasets

                        select new DatasetDto
                        {
                            Id = ds.Id,
                            Slug = ds.Slug,
                            CreationDate = ds.CreationDate,
                            Description = ds.Description,
                            LastEdit = ds.LastEdit,
                            Name = ds.Name,
                            License = ds.License,
                            Meta = ds.Meta,
                            ObjectsCount = ds.ObjectsCount,
                            Size = ds.Size
                        };

            return query;
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

            org.Datasets.Add(ds);

            await _context.SaveChangesAsync();

            return ds.ToDto();

        }

        public async Task Edit(string orgSlug, string dsSlug, DatasetDto dataset)
        {
            var org = await _utils.GetOrganization(orgSlug);

            var entity = org.Datasets.FirstOrDefault(item => item.Slug == dsSlug);

            if (entity == null)
                throw new NotFoundException("Dataset not found");

            entity.Description = dataset.Description;
            entity.IsPublic = dataset.IsPublic;
            entity.LastEdit = DateTime.Now;
            entity.License = dataset.License;
            entity.Meta = dataset.Meta;
            entity.Name = dataset.Name;

            if (!string.IsNullOrEmpty(dataset.Password))
                entity.PasswordHash = _passwordHasher.Hash(dataset.Password);

            await _context.SaveChangesAsync();

        }


        public async Task Delete(string orgSlug, string dsSlug)
        {
            var org = await _utils.GetOrganization(orgSlug);

            var entity = org.Datasets.FirstOrDefault(item => item.Slug == dsSlug);

            if (entity == null)
                throw new NotFoundException("Dataset not found");

            await _objectsManager.DeleteAll(orgSlug, dsSlug);
            _ddbManager.Delete(orgSlug, dsSlug);

            _context.Datasets.Remove(entity);

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

            ds.Slug = newSlug;

            await _context.SaveChangesAsync();

        }

        public async Task<Dictionary<string, object>> ChangeAttributes(string orgSlug, string dsSlug, Dictionary<string, object> attributes)
        {
            await _utils.GetDataset(orgSlug, dsSlug);

            var ddb = _ddbManager.Get(orgSlug, dsSlug);

            return ddb.ChangeAttributes(attributes);

        }
    }
}
