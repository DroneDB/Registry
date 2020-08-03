using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.DroneDB;
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
        private readonly IDdbFactory _ddbFactory;
        private readonly IAuthManager _authManager;
        private readonly IUtils _utils;
        private readonly RegistryContext _context;
        private readonly AppSettings _settings;

        private const string BucketNameFormat = "{0}-{1}";

        public ObjectsManager(ILogger<ObjectsManager> logger,
            RegistryContext context,
            IObjectSystem objectSystem,
            IOptions<AppSettings> settings,
            IDdbFactory ddbFactory,
            IAuthManager authManager,
            IUtils utils)
        {
            _logger = logger;
            _context = context;
            _objectSystem = objectSystem;
            _ddbFactory = ddbFactory;
            _authManager = authManager;
            _utils = utils;
            _settings = settings.Value;
        }

        public async Task<IEnumerable<ObjectDto>> List(string orgId, string dsId, string path)
        {

            await _utils.GetDatasetAndCheck(orgId, dsId);

            using var ddb = _ddbFactory.GetDdb(orgId, dsId);

            var files = ddb.Search(path);

            var query = files.Select(file => file.ToDto());

            return query;
        }

        public async Task<ObjectRes> Get(string orgId, string dsId, string path)
        {

            await _utils.GetDatasetAndCheck(orgId, dsId);

            var bucketName = string.Format(BucketNameFormat, orgId, dsId);

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
            {
                var region = _settings.StorageProvider.Settings.SafeGetValue("region");
                if (region == null)
                    _logger.LogWarning("No region specified in storage provider config");

                await _objectSystem.MakeBucketAsync(bucketName, region);
            }

            var objInfo = await _objectSystem.GetObjectInfoAsync(bucketName, path);

            if (objInfo == null)
                throw new NotFoundException($"Cannot find '{path}'");
            
            await using var memory = new MemoryStream();

            await _objectSystem.GetObjectAsync(bucketName, path, stream => stream.CopyTo(memory));

            return new ObjectRes
            {
                ContentType = objInfo.ContentType,
                Name = objInfo.ObjectName,
                Data = memory.ToArray()
            };

        }

        public async Task<UploadedObjectDto> AddNew(string orgId, string dsId, string path, byte[] data)
        {
            await _utils.GetDatasetAndCheck(orgId, dsId);

            var bucketName = string.Format(BucketNameFormat, orgId, dsId);

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
                throw new BadRequestException($"Cannot find bucket '{bucketName}'");

            // TODO: We should do something with DDB to add the file
            // using var ddb = _ddbFactory.GetDdb(orgId, dsId);
            // var file = ddb.Search(path).FirstOrDefault();
            // ddb.Add ?

            await using var memory = new MemoryStream(data);

            // TODO: I highly doubt the robustness of this 
            var contentType = MimeTypes.GetMimeType(path);

            // TODO: No metadata / encryption ?
            await _objectSystem.PutObjectAsync(bucketName, path, memory, data.Length, contentType);

            var obj = new UploadedObjectDto
            {
                Path = path,
                ContentType = contentType,
                Size = data.Length
            };
            
            return obj;
        }

        public async Task Delete(string orgId, string dsId, string path)
        {
            await _utils.GetDatasetAndCheck(orgId, dsId);

            var bucketName = string.Format(BucketNameFormat, orgId, dsId);

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
                throw new BadRequestException($"Cannot find bucket '{bucketName}'");

            await _objectSystem.RemoveObjectAsync(bucketName, path);

        }

        public async Task DeleteAll(string orgId, string dsId)
        {
            await _utils.GetDatasetAndCheck(orgId, dsId);

            var bucketName = string.Format(BucketNameFormat, orgId, dsId);

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists) {
                _logger.LogWarning($"Asked to remove non-existing bucket '{bucketName}'");
                return;
            }
            await _objectSystem.RemoveBucketAsync(bucketName);

        }
    }
}
