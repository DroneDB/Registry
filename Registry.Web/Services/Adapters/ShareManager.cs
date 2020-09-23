using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        // TODO: Implement queue
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

        public async Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug)
        {
            await _utils.GetOrganizationAndCheck(orgSlug);
            var dataset = await _utils.GetDatasetAndCheck(orgSlug, dsSlug);

            _logger.LogInformation($"Listing batches of '{orgSlug}/{dsSlug}'");

            var batches = from batch in _context.Batches
                    .Include(x => x.Entries)
                    .Include(x => x.Dataset)
                          where batch.Dataset.Id == dataset.Id
                          select new BatchDto
                          {
                              End = batch.End,
                              Start = batch.Start,
                              Token = batch.Token,
                              UserName = batch.UserName,
                              Status = batch.Status,
                              Entries = from entry in batch.Entries
                                        select new EntryDto
                                        {
                                            Hash = entry.Hash,
                                            Type = entry.Type,
                                            Size = entry.Size,
                                            AddedOn = entry.AddedOn,
                                            Path = entry.Path
                                        }
                          };


            return batches;
        }

        public async Task<string> Initialize(ShareInitDto parameters)
        {
            if (parameters == null)
                throw new BadRequestException("Invalid parameters");

            TagDto tag;

            try
            {
                tag = parameters.Tag.ToTag();
            }
            catch (FormatException ex)
            {
                throw new BadRequestException($"Invalid tag: {ex.Message}");
            }

            // Let's fill the names if not provided
            parameters.DatasetName ??= tag.DatasetSlug;

            var org = await _utils.GetOrganizationAndCheck(tag.OrganizationSlug, true);

            // Org must exist
            if (org == null)
            {
                throw new BadRequestException($"Organization '{tag.OrganizationSlug}' does not exist");
            }

            _logger.LogInformation("Organization found");

            // Create dataset if not exists
            var dataset = await _utils.GetDatasetAndCheck(tag.OrganizationSlug, tag.DatasetSlug, true);

            if (dataset == null)
            {
                _logger.LogInformation($"Dataset '{tag.DatasetSlug}' not found, creating it");

                await _datasetsManager.AddNew(tag.OrganizationSlug, new DatasetDto
                {
                    Slug = tag.DatasetSlug,
                    Name = parameters.DatasetName,
                    Description = parameters.DatasetDescription
                });
                dataset = await _utils.GetDatasetAndCheck(tag.OrganizationSlug, tag.DatasetSlug);

                _logger.LogInformation("Dataset created");
            }
            else
            {
                _logger.LogInformation("Dataset and organization already existing, checking for running batches");

                await _context.Entry(dataset).Collection(item => item.Batches).LoadAsync();

                var runningBatches = dataset.Batches.Where(item => item.End == null).ToArray();

                if (runningBatches.Any())
                {
                    _logger.LogInformation($"Found '{runningBatches.Length}' running batch(es), stopping and rolling back before starting a new one");

                    foreach (var b in runningBatches)
                        await RollbackBatch(b);

                }
            }

            var batch = new Batch
            {
                Dataset = dataset,
                Start = DateTime.Now,
                Token = CommonUtils.RandomString(TokenLength),
                UserName = await _authManager.SafeGetCurrentUserName(),
                Status = BatchStatus.Running
            };

            _logger.LogInformation($"Adding new batch for user '{batch.UserName}' with token '{batch.Token}'");

            await _context.Batches.AddAsync(batch);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Batch created, it is now possible to upload files");

            return batch.Token;
        }

        private async Task RollbackBatch(Batch batch)
        {
            _logger.LogInformation($"Rolling back batch '{batch.Token}'");
            
            // I know we will have concurrency problems here because we could be in the middle of the rollback when another client calls for another one
            // To mitigate this issue we are going to commit the status change of the batch as soon as possible

            batch.Status = BatchStatus.Rolledback;
            batch.End = DateTime.Now;
            await _context.SaveChangesAsync();

            await _context.Entry(batch).Collection(item => item.Entries).LoadAsync();
            await _context.Entry(batch).Reference(item => item.Dataset).LoadAsync();
            
            var ds = batch.Dataset;
            
            await _context.Entry(ds).Reference(item => item.Organization).LoadAsync();
            var org = ds.Organization;

            foreach (var entry in batch.Entries)
                await _objectsManager.Delete(org.Slug, ds.Slug, entry.Path);
            
        }


        public async Task<UploadResultDto> Upload(string token, string path, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("Missing token");

            if (string.IsNullOrWhiteSpace(path))
                throw new BadRequestException("Missing path");

            if (data == null || data.Length == 0)
                throw new BadRequestException("Missing data");

            var batch = _context.Batches
                .Include(x => x.Dataset.Organization)
                .Include(x => x.Entries)
                .FirstOrDefault(item => item.Token == token);

            if (batch == null)
                throw new NotFoundException("Cannot find batch");

            if (batch.Status != BatchStatus.Running)
                throw new BadRequestException("Cannot upload file to closed batch");

            var currentUserName = await _authManager.SafeGetCurrentUserName();

            if (!(await _authManager.IsUserAdmin() || batch.UserName == currentUserName))
                throw new BadRequestException("This batch does not belong to you");

            var entry = batch.Entries.FirstOrDefault(item => item.Path == path);

            if (entry != null)
                throw new BadRequestException("Entry already uploaded");

            _logger.LogInformation($"Adding '{path}' in batch '{batch.Token}'");

            var orgSlug = batch.Dataset.Organization.Slug;
            var dsSlug = batch.Dataset.Slug;

            await _objectsManager.AddNew(orgSlug, dsSlug, path, data);

            var info = (await _objectsManager.List(orgSlug, dsSlug, path)).FirstOrDefault();

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

            _logger.LogInformation("Entry added");

            await _context.SaveChangesAsync();

            _logger.LogInformation("Changes commited");

            return new UploadResultDto
            {
                Path = entry.Path
            };

        }

        public async Task<CommitResultDto> Commit(string token, bool rollback = false)
        {

            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("Missing token");

            var batch = _context.Batches.FirstOrDefault(item => item.Token == token);

            if (batch == null)
                throw new NotFoundException("Cannot find batch");

            if (batch.Status != BatchStatus.Running)
                throw new BadRequestException("Cannot commit a closed batch");

            var currentUserName = await _authManager.SafeGetCurrentUserName();

            if (!(await _authManager.IsUserAdmin() || batch.UserName == currentUserName))
                throw new BadRequestException("This batch does not belong to you");

            batch.End = DateTime.Now;

            if (rollback)
            {
                _logger.LogInformation($"Rolling back batch '{token}' @ {batch.End.Value.ToLongDateString()} {batch.End.Value.ToLongTimeString()}");

                await RollbackBatch(batch);

                _logger.LogInformation($"Batch '{token}' rolled back");

                batch.Status = BatchStatus.Rolledback;
            }
            else
            {
                _logger.LogInformation($"Committing batch '{token}' @ {batch.End.Value.ToLongDateString()} {batch.End.Value.ToLongTimeString()}");

                batch.Status = BatchStatus.Committed;

                // TODO: Are we supposed to do more operations here?

                _logger.LogInformation($"Batch '{token}' committed");

            }

            await _context.SaveChangesAsync();

            await _context.Entry(batch).Collection(item => item.Entries).LoadAsync();

            return new CommitResultDto
            {
                End = batch.End.Value,
                Start = batch.Start,
                ObjectsCount = batch.Entries.Count,
                TotalSize = batch.Entries.Sum(item => item.Size),
                Status = batch.Status
            };

        }
    }
}
