using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Registry.Adapters.ObjectSystem;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class SystemManager : ISystemManager
    {
        private readonly IChunkedUploadManager _chunkedUploadManager;
        private readonly IAuthManager _authManager;
        private readonly RegistryContext _context;
        private readonly IDdbManager _ddbManager;
        private readonly ILogger<SystemManager> _logger;
        private readonly IObjectSystem _objectSystem;
        private readonly IObjectsManager _objectManager;
        private readonly AppSettings _settings;

        public SystemManager(IChunkedUploadManager chunkedUploadManager, IAuthManager authManager,
            RegistryContext context, IDdbManager ddbManager, ILogger<SystemManager> logger, IObjectSystem objectSystem,
            IObjectsManager objectManager, IOptions<AppSettings> settings)
        {
            _chunkedUploadManager = chunkedUploadManager;
            _authManager = authManager;
            _context = context;
            _ddbManager = ddbManager;
            _logger = logger;
            _objectSystem = objectSystem;
            _objectManager = objectManager;
            _settings = settings.Value;
        }

        public async Task<CleanupResult> CleanupSessions()
        {

            if (!await _authManager.IsUserAdmin())
                throw new UnauthorizedException("Only admins can perform system related tasks");

            return new CleanupResult
            {
                RemovedSessions = (await _chunkedUploadManager.RemoveTimedoutSessions()).Union(
                    await _chunkedUploadManager.RemoveClosedSessions()).ToArray()
            };

        }

        public async Task<CleanupDatasetResultDto> CleanupEmptyDatasets()
        {
            if (!await _authManager.IsUserAdmin())
                throw new UnauthorizedException("Only admins can perform system related tasks");

            var datasets = _context.Datasets.Include(ds => ds.Organization).Where(ds => ds.ObjectsCount == 0).ToArray();

            _logger.LogInformation($"Found {datasets.Length} with objects count zero");

            var deleted = new List<string>();
            var notDeleted = new List<CleanupDatasetErrorDto>();

            foreach (var ds in datasets)
            {
                _logger.LogInformation($"Analyzing dataset {ds.Organization.Slug}/{ds.Slug}");

                try
                {
                    // Check if objects count is ok
                    var ddb = _ddbManager.Get(ds.Organization.Slug, ds.InternalRef);

                    var entries = ddb.Search("*", true)?.ToArray();

                    if (entries != null && entries.Any())
                    {
                        _logger.LogInformation($"Objects count was wrong, found {entries.Length} objects, updating stats and going on");
                        ds.ObjectsCount = entries.Length;
                        ds.Size = entries.Sum(entry => entry.Size);

                        await _context.SaveChangesAsync();
                    }
                    else
                    {
                        _context.Remove(ds);
                        await _context.SaveChangesAsync();

                        deleted.Add(ds.Slug);
                        _logger.LogInformation("Deleted");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Cannot remove dataset '{ds.Slug}'");
                    notDeleted.Add(new CleanupDatasetErrorDto
                    {
                        Dataset = ds.Slug,
                        Organization = ds.Organization.Slug,
                        Message = ex.Message
                    });
                }
            }

            return new CleanupDatasetResultDto
            {
                RemoveDatasetErrors = notDeleted.ToArray(),
                RemovedDatasets = deleted.ToArray()
            };

        }

        public string GetVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        }

        public async Task<CleanupBatchesResultDto> CleanupBatches()
        {
            if (!await _authManager.IsUserAdmin())
                throw new UnauthorizedException("Only admins can perform system related tasks");

            var expiration = DateTime.Now - _settings.UploadBatchTimeout;

            // I'm scared
            var toRemove = (from batch in _context.Batches
                    .Include(b => b.Dataset.Organization)
                    .Include(b => b.Entries)
                           where batch.Status == BatchStatus.Committed ||
                                 ((batch.Status == BatchStatus.Rolledback || batch.Status == BatchStatus.Running) &&
                                  batch.Entries.Max(entry => entry.AddedOn) < expiration)
                           select batch).ToArray();

            var removed = new List<RemovedBatchDto>();
            var errors = new List<RemoveBatchErrorDto>();

            foreach (var batch in toRemove)
            {

                var ds = batch.Dataset;
                var org = ds.Organization;

                try
                {

                    // Remove intermediate files
                    if (batch.Status == BatchStatus.Rolledback || batch.Status == BatchStatus.Running)
                    {

                        var entries = batch.Entries.ToArray();

                        foreach (var entry in entries)
                        {
                            _logger.LogInformation($"Deleting '{entry.Path}' of '{org.Slug}/{ds.Slug}'");
                            await _objectManager.Delete(org.Slug, ds.Slug, entry.Path);
                        }

                        var ddb = _ddbManager.Get(org.Slug, ds.InternalRef);

                        // Remove empty ddb
                        if (!ddb.Search("*", true).Any())
                            _ddbManager.Delete(org.Slug, ds.InternalRef);

                    }

                    _context.Batches.Remove(batch);

                    await _context.SaveChangesAsync();

                    removed.Add(new RemovedBatchDto
                    {
                        Status = batch.Status,
                        Start = batch.Start,
                        End = batch.End,
                        Token = batch.Token,
                        UserName = batch.UserName,
                        Dataset = ds.Slug,
                        Organization = org.Slug
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Cannot remove batch '{batch.Token}'");
                    errors.Add(new RemoveBatchErrorDto
                    {
                        Message = ex.Message,
                        Token = batch.Token,
                        Dataset = ds.Slug,
                        Organization = org.Slug
                    });
                }
            }
            
            return new CleanupBatchesResultDto
            {
                RemovedBatches = removed.ToArray(),
                RemoveBatchErrors = errors.ToArray()
            };

        }

        public async Task SyncDdbMeta(string[] orgs = null, bool skipAuthCheck = false)
        {

            if (!skipAuthCheck && !await _authManager.IsUserAdmin())
                throw new UnauthorizedException("Only admins can perform system related tasks");

            Tuple<string, Dataset>[] query;

            if (orgs == null)
            {
                query = (from ds in _context.Datasets.Include(item => item.Organization)
                         let org = ds.Organization
                         select new Tuple<string, Dataset>(org.Slug, ds)).ToArray();
            }
            else
            {

                query = (from ds in _context.Datasets.Include(item => item.Organization)
                         let org = ds.Organization
                         where orgs.Contains(org.Slug)
                         select new Tuple<string, Dataset>(org.Slug, ds)).ToArray();
            }

            foreach (var (orgSlug, ds) in query)
            {
                var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
                try
                {
                    var attrs = ddb.ChangeAttributes(new Dictionary<string, object>());

                    ds.Meta = attrs;

                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Cannot get attributes from ddb");
                }
            }

            await _context.SaveChangesAsync();
        }

        public SyncFilesResDto SyncFiles()
        {
            var cachedS3 = _objectSystem as CachedS3ObjectSystem;

            if (cachedS3 == null)
                throw new NotSupportedException(
                    "Current object system does not support SyncFiles method, only CachedS3ObjectSystem can");

            var res = cachedS3.SyncFiles();

            return new SyncFilesResDto
            {
                ErrorFiles = res?.ErrorFiles?.Select(err => new SyncFileErrorDto
                {
                    ErrorMessage = err.ErrorMessage,
                    Path = err.Path
                }).ToArray(),
                SyncedFiles = res?.SyncedFiles
            };
        }
    }
}
