using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
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

        public SystemManager(IChunkedUploadManager chunkedUploadManager, IAuthManager authManager, RegistryContext context, IDdbManager ddbManager, ILogger<SystemManager> logger)
        {
            _chunkedUploadManager = chunkedUploadManager;
            _authManager = authManager;
            _context = context;
            _ddbManager = ddbManager;
            _logger = logger;
        }

        public async Task<CleanupResult> CleanupSessions()
        {

            if (!await _authManager.IsUserAdmin())
                throw new UnauthorizedException("Only admins can perform system related tasks");
            
            var removedSessions = new List<int>();
            removedSessions.AddRange(await _chunkedUploadManager.RemoveTimedoutSessions());
            removedSessions.AddRange(await _chunkedUploadManager.RemoveClosedSessions());

            return new CleanupResult
            {
                RemovedSessions = removedSessions.ToArray()
            };

        }

        public async Task SyncDdbMeta(string[] orgs = null)
        {

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

                    ds.Meta = JsonConvert.SerializeObject(attrs);

                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Cannot get attributes from ddb");
                }
            }

            await _context.SaveChangesAsync();
        }

    }
}
