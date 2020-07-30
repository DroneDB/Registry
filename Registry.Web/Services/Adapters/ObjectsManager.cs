using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class ObjectsManager : IObjectsManager
    {
        private readonly ILogger<ObjectsManager> _logger;
        private readonly IObjectSystem _objectSystem;
        private readonly RegistryContext _context;
        private readonly AppSettings _settings;

        public ObjectsManager(ILogger<ObjectsManager> logger, RegistryContext context, IObjectSystem objectSystem, IOptions<AppSettings> settings)
        {
            _logger = logger;
            _context = context;
            _objectSystem = objectSystem;
            _settings = settings.Value;
        }

        public async Task<IEnumerable<ObjectDto>> List(string orgId, string dsId, string path)
        {
            if (string.IsNullOrWhiteSpace(orgId))
                throw new BadRequestException("Missing organization id");

            if (string.IsNullOrWhiteSpace(dsId))
                throw new BadRequestException("Missing dataset id");

            var bucketName = string.Format(orgId, dsId);

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
            {
                var region = _settings.StorageProvider.Settings.SafeGetValue("region");
                if (region == null)
                    _logger.LogWarning("No region specified in storage provider config");

                await _objectSystem.MakeBucketAsync(bucketName, region);
            }

            var list = _objectSystem.ListObjectsAsync(bucketName, path, true).ToEnumerable();

            /*
            var query = from item in list
                select new ObjectDto
                {
                    Size = item.Size,
                    
                };*/

            throw new NotImplementedException();
        }

        public async Task<ObjectRes> Get(string orgId, string dsId, string path)
        {
            throw new NotImplementedException();
        }

        public async Task<ObjectDto> AddNew(string orgId, string dsId, string path)
        {
            throw new NotImplementedException();
        }

        public async Task Delete(string orgId, string dsId, string path)
        {
            throw new NotImplementedException();
        }

    }
}
