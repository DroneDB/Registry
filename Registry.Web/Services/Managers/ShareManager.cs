using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Entry = Registry.Web.Data.Models.Entry;

namespace Registry.Web.Services.Managers
{
    public class ShareManager : IShareManager
    {
        private readonly IOrganizationsManager _organizationsManager;
        private readonly IDatasetsManager _datasetsManager;
        private readonly IObjectsManager _objectsManager;
        private readonly IOptions<AppSettings> _settings;
        private readonly ILogger<ShareManager> _logger;
        private readonly IUtils _utils;
        private readonly IAuthManager _authManager;
        private readonly IBatchTokenGenerator _batchTokenGenerator;
        private readonly INameGenerator _nameGenerator;
        private readonly RegistryContext _context;

        public ShareManager(
            IOptions<AppSettings> settings,
            ILogger<ShareManager> logger,
            IObjectsManager objectsManager,
            IDatasetsManager datasetsManager,
            IOrganizationsManager organizationsManager,
            IUtils utils,
            IAuthManager authManager,
            IBatchTokenGenerator batchTokenGenerator,
            INameGenerator nameGenerator,
            RegistryContext context)
        {
            _settings = settings;
            _logger = logger;
            _objectsManager = objectsManager;
            _datasetsManager = datasetsManager;
            _organizationsManager = organizationsManager;
            _utils = utils;
            _authManager = authManager;
            _batchTokenGenerator = batchTokenGenerator;
            _nameGenerator = nameGenerator;
            _context = context;
        }

        public async Task<BatchDto> GetBatchInfo(string token)
        {
            var batch = await GetRunningBatchFromToken(token);

            return new BatchDto
            {
                End = batch.End,
                Start = batch.Start,
                Token = batch.Token,
                UserName = batch.UserName,
                Status = batch.Status,
                Entries = from entry in batch.Entries
                    select new BatchEntryDto
                    {
                        Hash = entry.Hash,
                        Type = entry.Type,
                        Size = entry.Size,
                        AddedOn = entry.AddedOn,
                        Path = entry.Path
                    }
            };
        }

        private async Task<Batch> GetRunningBatchFromToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("Missing token");

            var batch = _context.Batches
                .Include(x => x.Dataset.Organization)
                .Include(x => x.Entries)
                .FirstOrDefault(item => item.Token == token);

            if (batch == null)
                throw new NotFoundException("Cannot find batch");

            if (batch.Status != BatchStatus.Running)
                throw new BadRequestException("Only running batches can be rollbacked");

            var currentUserName = await _authManager.SafeGetCurrentUserName();

            if (!(await _authManager.IsUserAdmin() || batch.UserName == currentUserName))
                throw new BadRequestException("This batch does not belong to you");

            return batch;
        }

        public async Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug)
        {
            await _utils.GetOrganization(orgSlug);
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

            _logger.LogInformation("Listing batches of '{OrgSlug}/{DsSlug}'", orgSlug, dsSlug);

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
                        select new BatchEntryDto
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

        public async Task<bool> IsPathAllowed(string token, string path)
        {
            var res = await IsBatchReady(token);

            if (!res.IsReady)
            {
                _logger.LogDebug("The batch '{Token}' is not ready", token);
                return false;
            }

            var entry = res.Batch.Entries.FirstOrDefault(item => item.Path == path);

            if (entry != null)
            {
                _logger.LogDebug("The batch '{Token}' already contains path '{Path}'", token, path);
                return false;
            }

            return true;
        }

        public async Task<IsBatchReadyResult> IsBatchReady(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("Missing token");

            var batch = _context.Batches
                .Include(x => x.Dataset.Organization)
                .Include(x => x.Entries)
                .FirstOrDefault(item => item.Token == token);

            if (batch == null)
            {
                _logger.LogDebug("Cannot find batch '{Token}'", token);
                return IsBatchReadyResult.NotReady;
            }

            if (batch.Status != BatchStatus.Running)
            {
                _logger.LogDebug("Cannot upload file to closed batch '{Token}'", token);
                return IsBatchReadyResult.NotReady;
            }

            var currentUserName = await _authManager.SafeGetCurrentUserName();

            if (!(await _authManager.IsUserAdmin() || batch.UserName == currentUserName))
            {
                _logger.LogDebug("The batch '{Token}' does not belong to you", token);
                return IsBatchReadyResult.NotReady;
            }

            return new IsBatchReadyResult(true, batch);
        }

        public async Task<ShareInitResultDto> Initialize(ShareInitDto parameters)
        {
            if (parameters == null)
                throw new BadRequestException("Invalid parameters");

            var currentUser = await _authManager.GetCurrentUser();
            if (currentUser == null)
                throw new UnauthorizedException("Invalid user");

            // Check if user has enough space to upload any file
            await _utils.CheckCurrentUserStorage();

            Dataset dataset;
            TagDto tag = parameters.Tag.ToTag();

            // No organization requested
            if (tag.OrganizationSlug == null)
            {
                if (tag.DatasetSlug != null)
                    throw new BadRequestException("Cannot specify a dataset without an organization");

                string orgSlug;

                _logger.LogInformation("No organization and dataset specified");

                var nameSlug = currentUser.UserName.ToSlug();

                // We start from the principle that a default user organization begins with the username in slug form
                // If we notice that this strategy is weak we have to add a new field to the db entry
                var userOrganization = _context.Organizations.FirstOrDefault(item =>
                    item.OwnerId == currentUser.Id && item.Name.StartsWith(nameSlug));

                // Some idiot removed its own organization, maybe we should prevent this, btw nevermind: let's take care of it
                if (userOrganization == null)
                {
                    // This section of code can be extracted and put in a separated utils method
                    orgSlug = _utils.GetFreeOrganizationSlug(currentUser.UserName);

                    _logger.LogInformation("No default user organization found, adding a new one: '{OrgSlug}'",
                        orgSlug);

                    var org = await _organizationsManager.AddNew(new OrganizationDto
                    {
                        Name = currentUser.UserName,
                        IsPublic = false,
                        CreationDate = DateTime.Now,
                        Owner = currentUser.Id,
                        Slug = orgSlug
                    });

                    userOrganization = _context.Organizations.First(item => item.Slug == orgSlug);
                }

                orgSlug = userOrganization.Slug;

                _logger.LogInformation("Using default user organization '{OrgSlug}'", orgSlug);

                var dsSlug = GetUniqueDatasetSlug();

                _logger.LogInformation("Generated unique dataset slug '{DsSlug}'", dsSlug);

                await _datasetsManager.AddNew(orgSlug, new DatasetNewDto
                {
                    Slug = dsSlug,
                    Name = parameters.DatasetName,
                    IsPublic = true
                });

                dataset = await _utils.GetDataset(orgSlug, dsSlug);

                _logger.LogInformation("Created new dataset '{DsSlug}', creating batch", dsSlug);
            }
            else
            {
                // Check if the requested organization exists
                var organization = await _utils.GetOrganization(tag.OrganizationSlug, true);

                if (organization == null)
                    throw new BadRequestException($"Cannot find organization '{tag.OrganizationSlug}'");

                _logger.LogInformation("Organization found");

                // If no dataset is specified, we create a new one with a random slug
                if (tag.DatasetSlug == null)
                {
                    var dsSlug = GetUniqueDatasetSlug();

                    _logger.LogInformation("Generated unique dataset slug '{DsSlug}'", dsSlug);

                    await _datasetsManager.AddNew(tag.OrganizationSlug, new DatasetNewDto
                    {
                        Slug = dsSlug,
                        Name = parameters.DatasetName,
                        IsPublic = true
                    });

                    dataset = _context.Datasets.First(ds => ds.Slug == dsSlug);

                    _logger.LogInformation("Dataset created");
                }
                else
                {
                    dataset = await _utils.GetDataset(tag.OrganizationSlug, tag.DatasetSlug, true);

                    // Create dataset if not exists
                    if (dataset == null)
                    {
                        _logger.LogInformation("Dataset '{DatasetSlug}' not found, creating it", tag.DatasetSlug);

                        await _datasetsManager.AddNew(tag.OrganizationSlug, new DatasetNewDto
                        {
                            Slug = tag.DatasetSlug,
                            Name = parameters.DatasetName,
                            IsPublic = true
                        });

                        _logger.LogInformation("Dataset created");

                        dataset = _context.Datasets.First(ds => ds.Slug == tag.DatasetSlug);
                    }
                    else
                    {
                        _logger.LogInformation("Checking for running batches");
                        await _context.Entry(dataset).Collection(item => item.Batches).LoadAsync();

                        var runningBatches = dataset.Batches.Where(item => item.End == null).ToArray();

                        if (runningBatches.Any())
                        {
                            _logger.LogInformation(
                                "Found '{RunningBatchesCount}' running batch(es), stopping and rolling back before starting a new one",
                                runningBatches.Length);

                            foreach (var b in runningBatches)
                                await RollbackBatch(b);
                        }
                    }
                }
            }

            var batch = new Batch
            {
                Dataset = dataset,
                Start = DateTime.Now,
                Token = _batchTokenGenerator.GenerateToken(),
                UserName = await _authManager.SafeGetCurrentUserName(),
                Status = BatchStatus.Running
            };

            _logger.LogInformation("Adding new batch for user '{BatchUserName}' with token '{BatchToken}'",
                batch.UserName, batch.Token);

            await _context.Batches.AddAsync(batch);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Batch created, it is now possible to upload files");

            return new ShareInitResultDto
            {
                Token = batch.Token

                // NOTE: Maybe useful in the future
                // Tag = tag
            };
        }

        private string GetUniqueDatasetSlug()
        {
            // We are pretty sure this is unique but ALWAYS CHECK
            string dsSlug;
            do
            {
                // NOTE: We could generate a more language friendly slug, like docker does for its running containers
                dsSlug = _nameGenerator.GenerateName().ToSlug();
            } while (_context.Datasets.FirstOrDefault(item => item.Slug == dsSlug) != null);

            return dsSlug;
        }

        private async Task RollbackBatch(Batch batch, bool deleteDataset = false)
        {
            _logger.LogInformation("Rolling back batch '{BatchToken}'", batch.Token);

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

            if (deleteDataset)
                await _datasetsManager.Delete(org.Slug, ds.Slug);
            else
            {
                foreach (var entry in batch.Entries)
                    await _objectsManager.Delete(org.Slug, ds.Slug, entry.Path);
            }
        }

        public async Task Rollback(string token)
        {
            var batch = await GetRunningBatchFromToken(token);

            _logger.LogInformation("Rolling back  batch '{Token}'", token);

            await RollbackBatch(batch, true);
        }


        public async Task<UploadResultDto> Upload(string token, string path, byte[] data)
        {
            await using var stream = new MemoryStream(data);
            return await Upload(token, path, stream);
        }

        public async Task<UploadResultDto> Upload(string token, string path, Stream stream)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new BadRequestException("Missing path");

            if (stream == null)
                throw new BadRequestException("Missing data stream");

            if (!stream.CanRead)
                throw new BadRequestException("Cannot read from data stream");

            var batch = await GetRunningBatchFromToken(token);

            // Check if user has enough space to upload this
            await _utils.CheckCurrentUserStorage(stream.Length);

            var entry = batch.Entries.FirstOrDefault(item => item.Path == path);

            if (entry != null)
                throw new BadRequestException("Entry already uploaded");

            _logger.LogInformation("Adding '{Path}' in batch '{BatchToken}'", path, batch.Token);

            var orgSlug = batch.Dataset.Organization.Slug;
            var dsSlug = batch.Dataset.Slug;

            await _objectsManager.AddNew(orgSlug, dsSlug, path, stream);

            var info = (await _objectsManager.List(orgSlug, dsSlug, path)).FirstOrDefault();

            if (info == null)
                throw new BadRequestException(
                    "Underlying object storage is not working correctly: cannot find object after adding it");

            entry = new Entry
            {
                Type = info.Type,
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
                Hash = entry.Hash,
                Size = entry.Size,
                Path = entry.Path
            };
        }

        public async Task<CommitResultDto> Commit(string token)
        {
            var batch = await GetRunningBatchFromToken(token);

            _logger.LogInformation("Committing batch '{Token}' @ {BatchEndDate} {BatchEndTime}", token,
                batch.End?.ToLongDateString(), batch.End?.ToLongTimeString());

            batch.End = DateTime.Now;
            batch.Status = BatchStatus.Committed;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Batch '{Token}' committed", token);

            await _context.Entry(batch).Collection(item => item.Entries).LoadAsync();

            return new CommitResultDto
            {
                // NOTE: This url structure is so buried in the code that it's hard to find it, we should consider a better way to expose it
                Url = $"/r/{batch.Dataset.Organization.Slug}/{batch.Dataset.Slug}",
                Tag = new TagDto(batch.Dataset.Organization.Slug, batch.Dataset.Slug)
            };
        }
    }
}