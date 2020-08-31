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

namespace Registry.Web.Services.Adapters
{
    public class DatasetsManager : IDatasetsManager
    {
        private readonly IAuthManager _authManager;
        private readonly RegistryContext _context;
        private readonly IUtils _utils;
        private readonly ILogger<DatasetsManager> _logger;
        private readonly IObjectsManager _objectsManager;
        private readonly IDdbFactory _ddbFactory;
        private readonly IPasswordHasher _passwordHasher;

        // TODO: Add extensive testing
        
        public DatasetsManager(
            IAuthManager authManager,
            RegistryContext context,
            IUtils utils,
            ILogger<DatasetsManager> logger,
            IObjectsManager objectsManager,
            IDdbFactory ddbFactory,
            IPasswordHasher passwordHasher)
        {
            _authManager = authManager;
            _context = context;
            _utils = utils;
            _logger = logger;
            _objectsManager = objectsManager;
            _ddbFactory = ddbFactory;
            _passwordHasher = passwordHasher;
        }

        public async Task<IEnumerable<DatasetDto>> List(string orgId)
        {
            var org = await _utils.GetOrganizationAndCheck(orgId);

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

        public async Task<DatasetDto> Get(string orgId, string ds)
        {

            var dataset = await _utils.GetDatasetAndCheck(orgId, ds);

            return dataset.ToDto();
        }

        public async Task<DatasetDto> AddNew(string orgId, DatasetDto dataset)
        {

            var org = await _utils.GetOrganizationAndCheck(orgId);

            var ds = dataset.ToEntity();

            ds.LastEdit = DateTime.Now;
            ds.CreationDate = ds.LastEdit;

            if (!string.IsNullOrEmpty(dataset.Password)) 
                ds.PasswordHash = _passwordHasher.Hash(dataset.Password);

            org.Datasets.Add(ds);

            await _context.SaveChangesAsync();
            
            return ds.ToDto();

        }

        public async Task Edit(string orgId, string ds, DatasetDto dataset)
        {
            var org = await _utils.GetOrganizationAndCheck(orgId);

            var entity = org.Datasets.FirstOrDefault(item => item.Slug == ds);

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

        public async Task Delete(string orgId, string ds)
        {
            var org = await _utils.GetOrganizationAndCheck(orgId);
            
            var entity = org.Datasets.FirstOrDefault(item => item.Slug == ds);

            if (entity == null)
                throw new NotFoundException("Dataset not found");

            _context.Datasets.Remove(entity);

            await _objectsManager.DeleteAll(orgId, ds);

            await _context.SaveChangesAsync();
        }
    }
}
