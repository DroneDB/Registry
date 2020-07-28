using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

        // TODO: Add extensive logging
        // TODO: Add extensive testing
        
        public DatasetsManager(
            IAuthManager authManager,
            RegistryContext context,
            IUtils utils,
            ILogger<DatasetsManager> logger)
        {
            _authManager = authManager;
            _context = context;
            _utils = utils;
            _logger = logger;
        }

        public async Task<IEnumerable<DatasetDto>> GetAll(string orgId)
        {
            var currentUser = await _authManager.GetCurrentUser();

            if (currentUser == null)
                throw new UnauthorizedException("Invalid user");

            var org = _context.Organizations.Include(item => item.Datasets).FirstOrDefault(item => item.Id == orgId);

            if (org == null)
                throw new NotFoundException("Organization not found");

            if (!await _authManager.IsUserAdmin() && !(currentUser.Id == org.OwnerId || org.OwnerId == null))
                throw new UnauthorizedException("This organization does not belong to the current user");

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

            // TODO: To change when implementing anonymous users
            var currentUser = await _authManager.GetCurrentUser();

            if (currentUser == null)
                throw new UnauthorizedException("Invalid user");

            var org = _context.Organizations.Include(item => item.Datasets).FirstOrDefault(item => item.Id == orgId);

            if (org == null)
                throw new NotFoundException("Organization not found");

            if (!await _authManager.IsUserAdmin() && !(currentUser.Id == org.OwnerId || org.OwnerId == null))
                throw new UnauthorizedException("This organization does not belong to the current user");

            var dataset = org.Datasets.FirstOrDefault(item => item.Slug == ds);

            if (dataset == null)
                throw new NotFoundException("Cannot find dataset");

            return new DatasetDto(dataset);
        }

        public async Task<DatasetDto> AddNew(string orgId, DatasetDto dataset)
        {

            // TODO: To change when implementing anonymous users
            var currentUser = await _authManager.GetCurrentUser();

            if (currentUser == null)
                throw new UnauthorizedException("Invalid user");

            var org = _context.Organizations.Include(item => item.Datasets).FirstOrDefault(item => item.Id == orgId);

            if (org == null)
                throw new NotFoundException("Organization not found");
            
            if (!await _authManager.IsUserAdmin() && currentUser.Id != org.OwnerId) 
                throw new UnauthorizedException("This organization does not belong to this user");

            var ds = dataset.ToEntity();

            org.Datasets.Add(ds);

            await _context.SaveChangesAsync();

            return new DatasetDto(ds);

        }

        public async Task Edit(string orgId, string ds, DatasetDto dataset)
        {
            // TODO: To change when implementing anonymous users
            var currentUser = await _authManager.GetCurrentUser();

            if (currentUser == null)
                throw new UnauthorizedException("Invalid user");

            var org = _context.Organizations.Include(item => item.Datasets).FirstOrDefault(item => item.Id == orgId);

            if (org == null)
                throw new NotFoundException("Organization not found");

            if (!await _authManager.IsUserAdmin() && currentUser.Id != org.OwnerId)
                throw new UnauthorizedException("This organization does not belong to this user");

            var entity = org.Datasets.FirstOrDefault(item => item.Slug == ds);

            if (entity == null)
                throw new NotFoundException("Dataset not found");

            entity.Description = dataset.Description;
            entity.IsPublic = dataset.IsPublic;
            entity.LastEdit = DateTime.Now;
            entity.License = dataset.License;
            entity.Meta = dataset.Meta;
            entity.Name = dataset.Name;

            await _context.SaveChangesAsync();

        }

        public async Task Delete(string orgId, string ds)
        {
            // TODO: To change when implementing anonymous users
            var currentUser = await _authManager.GetCurrentUser();

            if (currentUser == null)
                throw new UnauthorizedException("Invalid user");

            var org = _context.Organizations.Include(item => item.Datasets).FirstOrDefault(item => item.Id == orgId);

            if (org == null)
                throw new NotFoundException("Organization not found");

            if (!await _authManager.IsUserAdmin() && currentUser.Id != org.OwnerId)
                throw new UnauthorizedException("This organization does not belong to this user");

            var entity = org.Datasets.FirstOrDefault(item => item.Slug == ds);

            if (entity == null)
                throw new NotFoundException("Dataset not found");

            _context.Datasets.Remove(entity);

            await _context.SaveChangesAsync();
        }
    }
}
