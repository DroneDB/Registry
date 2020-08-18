using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class ShareManager : IShareManager
    {

        private readonly IOrganizationsManager _organizationsManager;
        private readonly IDatasetsManager _datasetsManager;
        private readonly IObjectsManager _objectsManager;
        private readonly ILogger<ShareManager> _logger;
        private readonly IUtils _utils;
        private readonly IAuthManager _authManager;
        private readonly RegistryContext _context;

        private const int TokenLength = 32;

        public ShareManager(
            ILogger<ShareManager> logger, 
            IObjectsManager objectsManager, 
            IDatasetsManager datasetsManager, 
            IOrganizationsManager organizationsManager, 
            IUtils utils,
            IAuthManager authManager,
            RegistryContext context)
        {
            _logger = logger;
            _objectsManager = objectsManager;
            _datasetsManager = datasetsManager;
            _organizationsManager = organizationsManager;
            _utils = utils;
            _authManager = authManager;
            _context = context;
        }

        public async Task<string> Initialize(ShareInitDto parameters)
        {

            if (parameters?.Dataset == null || parameters.Organization == null)
                throw new BadRequestException("Invalid parameters");
            
            var orgId = parameters.Organization?.Id;

            if (orgId == null)
                throw new BadRequestException("Organization id not provided");

            var dsSlug = parameters.Dataset?.Slug;
            
            if (dsSlug == null)
                throw new BadRequestException("Dataset slug not provided");

            var org = await _utils.GetOrganizationAndCheck(orgId, true);
            Dataset dataset;

            // Create org if not exists
            if (org == null)
            {

                await _organizationsManager.AddNew(parameters.Organization);
                await _datasetsManager.AddNew(orgId, parameters.Dataset);

                dataset = await _utils.GetDatasetAndCheck(orgId, dsSlug);

            }
            else
            {
                // Create dataset if not exists
                dataset = await _utils.GetDatasetAndCheck(orgId, dsSlug);

                if (dataset == null) {
                    await _datasetsManager.AddNew(orgId, parameters.Dataset);
                    dataset = await _utils.GetDatasetAndCheck(orgId, dsSlug);
                }

            }

            var batch = new Batch
            {
                Dataset = dataset,
                Start = DateTime.Now,
                Token = CommonUtils.RandomString(TokenLength),
                UserName = await _authManager.SafeGetCurrentUserName()
            };

            await _context.Batches.AddAsync(batch);
            await _context.SaveChangesAsync();

            return batch.Token;
        }

        public async Task Upload(string token, string path, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("Missing token");

            if (string.IsNullOrWhiteSpace(path))
                throw new BadRequestException("Missing path");

            if (data == null || data.Length == 0)
                throw new BadRequestException("Missing data");

            var batch = _context.Batches.Include(x => x.Dataset.Organization)
                .FirstOrDefault(item => item.Token == token);

            if (batch == null)
                throw new NotFoundException("Cannot find batch");

            var currentUserName = await _authManager.SafeGetCurrentUserName();

            if (!(await _authManager.IsUserAdmin() || batch.UserName == currentUserName))
                throw new BadRequestException("This batch does not belong to you");
            
            var entry = batch.Entries.FirstOrDefault(item => item.Path == path);

            if (entry != null)
                throw new BadRequestException("Entry already uploaded");
           

            var orgId = batch.Dataset.Organization.Id;
            var dsSlug = batch.Dataset.Slug;

            await _objectsManager.AddNew(orgId, dsSlug, path, data);

            var info = (await _objectsManager.List(orgId, dsSlug, path)).FirstOrDefault();

            if (info == null)
                throw new BadRequestException("Underlying object storage is not working correctly: cannot find object after adding it");

            entry = new Entry
            {
                Type = (EntryType)(int)info.Type,
                Hash = info.Hash,
                AddedOn = DateTime.Now,
                Path = path,
                Size = info.Size,
                Batch = batch
            };

            await _context.AddAsync(entry);

            await _context.SaveChangesAsync();

        }

        public async Task Commit(string token)
        {

            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("Missing token");

            var batch = _context.Batches.FirstOrDefault(item => item.Token == token);

            if (batch == null)
                throw new NotFoundException("Cannot find batch");

            var currentUserName = await _authManager.SafeGetCurrentUserName();

            if (!(await _authManager.IsUserAdmin() || batch.UserName == currentUserName))
                throw new BadRequestException("This batch does not belong to you");

            batch.End = DateTime.Now;

            await _context.SaveChangesAsync();

        }
    }
}
