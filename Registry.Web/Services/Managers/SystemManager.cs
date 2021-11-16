using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Adapters.ObjectSystem;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers
{
    public class SystemManager : ISystemManager
    {
        private readonly IAuthManager _authManager;
        private readonly RegistryContext _context;
        private readonly IDdbManager _ddbManager;
        private readonly ILogger<SystemManager> _logger;
        private readonly IObjectsManager _objectManager;
        private readonly AppSettings _settings;

        public SystemManager(IAuthManager authManager,
            RegistryContext context, IDdbManager ddbManager, ILogger<SystemManager> logger,
            IObjectsManager objectManager, IOptions<AppSettings> settings)
        {
            _authManager = authManager;
            _context = context;
            _ddbManager = ddbManager;
            _logger = logger;
            _objectManager = objectManager;
            _settings = settings.Value;
        }

        public async Task<CleanupDatasetResultDto> CleanupEmptyDatasets()
        {
            if (!await _authManager.IsUserAdmin())
                throw new UnauthorizedException("Only admins can perform system related tasks");

            var datasets = _context.Datasets.Include(ds => ds.Organization).ToArray();

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

                    var entries = (await ddb.SearchAsync("*", true))?.ToArray();

                    if (entries == null || !entries.Any())
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
                    if (batch.Status is BatchStatus.Rolledback or BatchStatus.Running)
                    {

                        var entries = batch.Entries.ToArray();

                        foreach (var entry in entries)
                        {
                            _logger.LogInformation($"Deleting '{entry.Path}' of '{org.Slug}/{ds.Slug}'");
                            await _objectManager.Delete(org.Slug, ds.Slug, entry.Path);
                        }

                        var ddb = _ddbManager.Get(org.Slug, ds.InternalRef);

                        // Remove empty ddb
                        if (!(await ddb.SearchAsync("*", true)).Any())
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

        public async Task Cleanup()
        {
            if (_objectSystem is not CachedS3ObjectSystem cachedS3)
                throw new NotSupportedException(
                    "Current object system does not support Cleanup method, only CachedS3ObjectSystem does");

            await cachedS3.Cleanup();

        }
    }
}
