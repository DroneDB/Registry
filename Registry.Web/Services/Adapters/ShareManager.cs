using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters
{
    public class ShareManager : IShareManager
    {

        private readonly IOrganizationsManager _organizationsManager;
        private readonly IDatasetsManager _datasetsManager;
        private readonly IObjectsManager _objectsManager;
        private readonly IChunkedUploadManager _chunkedUploadManager;
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
            IChunkedUploadManager chunkedUploadManager,
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
            _chunkedUploadManager = chunkedUploadManager;
        }

        public async Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug)
        {
            await _utils.GetOrganization(orgSlug);
            var dataset = await _utils.GetDataset(orgSlug, dsSlug);

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
                _logger.LogDebug($"The batch '{token}' is not ready");
                return false;
            }
            
            var entry = res.Batch.Entries.FirstOrDefault(item => item.Path == path);

            if (entry != null)
            {
                _logger.LogDebug($"The batch '{token}' already contains path '{path}'");
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
                _logger.LogDebug($"Cannot find batch '{token}'");
                return IsBatchReadyResult.NotReady;
            }

            if (batch.Status != BatchStatus.Running)
            {
                _logger.LogDebug($"Cannot upload file to closed batch '{token}'");
                return IsBatchReadyResult.NotReady;
            }

            var currentUserName = await _authManager.SafeGetCurrentUserName();

            if (!(await _authManager.IsUserAdmin() || batch.UserName == currentUserName))
            {
                _logger.LogDebug($"The batch '{token}' does not belong to you");
                return IsBatchReadyResult.NotReady;
            }

            return new IsBatchReadyResult(true, batch);
            
        }

        public async Task<int> StartUploadSession(string token, int chunks, long size)
        {
            var res = await IsBatchReady(token);

            if (!res.IsReady)
                throw new ArgumentException($"Batch '{token}' is not ready");

            var fileName = $"{token}-{CommonUtils.RandomString(16)}";

            _logger.LogDebug($"Generated '{fileName}' as temp file name");

            var sessionId = _chunkedUploadManager.InitSession(fileName, chunks, size);

            return sessionId;
        }

        public async Task UploadToSession(string token, int sessionId, int index, Stream stream)
        {
            var res = await IsBatchReady(token);

            if (!res.IsReady)
                throw new ArgumentException($"Batch '{token}' is not ready");

            await _chunkedUploadManager.Upload(sessionId, stream, index);

        }

        public async Task UploadToSession(string token, int sessionId, int index, byte[] data)
        {
            await using var memory = new MemoryStream();
            memory.Reset();
            await UploadToSession(token, sessionId, index, memory);
        }

        public async Task<UploadResultDto> CloseUploadSession(string token, int sessionId, string path)
        {
            var res = await IsPathAllowed(token, path);

            if (!res)
                throw new ArgumentException($"Batch '{token}' is not ready or path '{path}' is not allowed");

            var tempFilePath = _chunkedUploadManager.CloseSession(sessionId, false);

            UploadResultDto ret;

            await using (var fileStream = File.OpenRead(tempFilePath))
            {
                ret = await Upload(token, path, fileStream);
            }

            _chunkedUploadManager.CleanupSession(sessionId);

            File.Delete(tempFilePath);

            return ret;
        }


        public async Task<ShareInitResultDto> Initialize(ShareInitDto parameters)
        {
            if (parameters == null)
                throw new BadRequestException("Invalid parameters");

            var currentUser = await _authManager.GetCurrentUser(); ;
            if (currentUser == null)
                throw new UnauthorizedException("Invalid user");

            Dataset dataset;
            TagDto tag;

            if (parameters.Tag == null)
            {

                _logger.LogInformation("No tag provided, generating a new one");

                var nameSlug = currentUser.UserName.ToSlug();
                
                // We start from the principle that a default user organization begins with the username in slug form
                // If we notice that this strategy is weak we have to add a new field to the db entry
                var userOrganization = _context.Organizations.FirstOrDefault(item => item.OwnerId == currentUser.Id && item.Name.StartsWith(nameSlug));

                string orgSlug;

                // Some idiot removed its own organization, maybe we should prevent this, btw nevermind: let's take care of it
                if (userOrganization == null)
                {

                    // This section of code can be extracted and put in a separated utils method
                    orgSlug = _utils.GetFreeOrganizationSlug(currentUser.UserName);

                    _logger.LogInformation($"No default user organization found, adding a new one: '{orgSlug}'");

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

                _logger.LogInformation($"Using default user organization '{orgSlug}'");
                
                // We are pretty sure this is unique but ALWAYS CHECK
                string dsSlug;
                do
                {
                    // NOTE: We could generate a more language friendly slug, like docker does for its running containers
                    dsSlug = _nameGenerator.GenerateName().ToSlug();
                } while (_context.Datasets.FirstOrDefault(item => item.Slug == dsSlug) != null);

                _logger.LogInformation($"Generated unique dataset slug '{dsSlug}'");
                
                await _datasetsManager.AddNew(orgSlug, new DatasetDto
                {
                    Slug = dsSlug,
                    Name = parameters.DatasetName,
                    Description = parameters.DatasetDescription,
                    IsPublic = true
                });

                dataset = await _utils.GetDataset(orgSlug, dsSlug);

                _logger.LogInformation($"Created new dataset '{dsSlug}', creating batch");

                tag = new TagDto(orgSlug, dsSlug);

            }
            else
            {

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

                var org = await _utils.GetOrganization(tag.OrganizationSlug, true);

                // Org must exist
                if (org == null)
                {
                    throw new BadRequestException($"Organization '{tag.OrganizationSlug}' does not exist");
                }

                _logger.LogInformation("Organization found");

                // Create dataset if not exists
                dataset = await _utils.GetDataset(tag.OrganizationSlug, tag.DatasetSlug, true);

                if (dataset == null)
                {
                    _logger.LogInformation($"Dataset '{tag.DatasetSlug}' not found, creating it");

                    await _datasetsManager.AddNew(tag.OrganizationSlug, new DatasetDto
                    {
                        Slug = tag.DatasetSlug,
                        Name = parameters.DatasetName,
                        Description = parameters.DatasetDescription,
                        IsPublic = true
                    });
                    dataset = await _utils.GetDataset(tag.OrganizationSlug, tag.DatasetSlug);

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
            }

            var batch = new Batch
            {
                Dataset = dataset,
                Start = DateTime.Now,
                Token = _batchTokenGenerator.GenerateToken(),
                UserName = await _authManager.SafeGetCurrentUserName(),
                Status = BatchStatus.Running
            };

            _logger.LogInformation($"Adding new batch for user '{batch.UserName}' with token '{batch.Token}'");

            await _context.Batches.AddAsync(batch);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Batch created, it is now possible to upload files");

            return new ShareInitResultDto
            {
                Token = batch.Token, 
                MaxUploadChunkSize = _settings.Value.MaxUploadChunkSize,

                // NOTE: Maybe useful in the future
                // Tag = tag
            };
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
            await using var stream = new MemoryStream(data);
            return await Upload(token, path, stream);
        }

        public async Task<UploadResultDto> Upload(string token, string path, Stream stream)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("Missing token");

            if (string.IsNullOrWhiteSpace(path))
                throw new BadRequestException("Missing path");

            if (stream == null)
                throw new BadRequestException("Missing data stream");

            if (!stream.CanRead)
                throw new BadRequestException("Cannot read from data stream");

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

            await _objectsManager.AddNew(orgSlug, dsSlug, path, stream);

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
                Hash = entry.Hash,
                Size = entry.Size,
                Path = entry.Path
            };
        }

        public async Task<CommitResultDto> Commit(string token, bool rollback = false)
        {

            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("Missing token");

            var batch = _context.Batches
                .Include(x => x.Dataset)
                .Include(x => x.Dataset.Organization)
                .FirstOrDefault(item => item.Token == token);

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
                Url = "/r/" + batch.Dataset.Organization.Slug + "/" + batch.Dataset.Slug,
                Tag = new TagDto(batch.Dataset.Organization.Slug, batch.Dataset.Slug)
            };
        }
    }
}
