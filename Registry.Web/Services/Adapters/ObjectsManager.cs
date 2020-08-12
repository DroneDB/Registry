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

        // TODO: Add sqlite db sync to backing server

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

            _logger.LogInformation($"In '{orgId}/{dsId}'");

            using var ddb = _ddbFactory.GetDdb(orgId, dsId);

            _logger.LogInformation($"Searching in '{path}'");

            var files = ddb.Search(path);

            return files.Select(file => file.ToDto());
        }

        public async Task<ObjectRes> Get(string orgId, string dsId, string path)
        {

            await _utils.GetDatasetAndCheck(orgId, dsId);

            _logger.LogInformation($"In '{orgId}/{dsId}'");

            using var ddb = _ddbFactory.GetDdb(orgId, dsId);

            var res = ddb.Search(path).FirstOrDefault();

            if (res == null)
                throw new NotFoundException($"Cannot find '{path}'");

            var bucketName = string.Format(BucketNameFormat, orgId, dsId);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
            {
                _logger.LogInformation("Bucket does not exist, creating it");

                var region = _settings.StorageProvider.Settings.SafeGetValue("region");
                if (region == null)
                    _logger.LogWarning("No region specified in storage provider config");

                await _objectSystem.MakeBucketAsync(bucketName, region);

                _logger.LogInformation("Bucket created");

            }

            var objInfo = await _objectSystem.GetObjectInfoAsync(bucketName, path);

            if (objInfo == null)
                throw new NotFoundException($"Cannot find '{path}' in storage provider");
            
            await using var memory = new MemoryStream();

            _logger.LogInformation($"Getting object '{path}' in bucket '{bucketName}'");

            await _objectSystem.GetObjectAsync(bucketName, path, stream => stream.CopyTo(memory));
            
            return new ObjectRes
            {
                ContentType = objInfo.ContentType,
                Name = objInfo.ObjectName,
                Data = memory.ToArray(),
                // TODO: We can add more fields from DDB if we need them
                Type = (ObjectType)(int)res.Type
            };

        }

        public async Task<UploadedObjectDto> AddNew(string orgId, string dsId, string path, byte[] data)
        {
            await _utils.GetDatasetAndCheck(orgId, dsId);

            _logger.LogInformation($"In '{orgId}/{dsId}'");

            var bucketName = string.Format(BucketNameFormat, orgId, dsId);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
                throw new BadRequestException($"Cannot find bucket '{bucketName}'");

            await using var memory = new MemoryStream(data);

            // TODO: I highly doubt the robustness of this 
            var contentType = MimeTypes.GetMimeType(path);

            _logger.LogInformation($"Uploading '{path}' (size {data.Length}) to bucket '{bucketName}'");

            // TODO: No metadata / encryption ?
            await _objectSystem.PutObjectAsync(bucketName, path, memory, data.Length, contentType);

            _logger.LogInformation("File uploaded, adding to DDB");

            // Add to DDB
            using var ddb = _ddbFactory.GetDdb(orgId, dsId);
            ddb.Add(path, data);

            _logger.LogInformation("Added to DDB");

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

            _logger.LogInformation($"In '{orgId}/{dsId}'");

            var bucketName = string.Format(BucketNameFormat, orgId, dsId);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
                throw new BadRequestException($"Cannot find bucket '{bucketName}'");

            _logger.LogInformation($"Deleting '{path}'");

            await _objectSystem.RemoveObjectAsync(bucketName, path);

            _logger.LogInformation($"File deleted, removing from DDB");

            // Remove from DDB
            using var ddb = _ddbFactory.GetDdb(orgId, dsId);
            ddb.Remove(path);

            _logger.LogInformation("Removed from DDB");
            
        }

        public async Task DeleteAll(string orgId, string dsId)
        {
            await _utils.GetDatasetAndCheck(orgId, dsId);

            _logger.LogInformation($"In '{orgId}/{dsId}'");

            var bucketName = string.Format(BucketNameFormat, orgId, dsId);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists) {
                _logger.LogWarning($"Asked to remove non-existing bucket '{bucketName}'");
                return;
            }

            _logger.LogInformation($"Deleting bucket");

            await _objectSystem.RemoveBucketAsync(bucketName);

            _logger.LogInformation($"Bucket deleted, removing all files from DDB ");
            
            // Remove all from DDB
            using var ddb = _ddbFactory.GetDdb(orgId, dsId);

            var res = ddb.Search(null);
            foreach(var item in res)
                ddb.Remove(item.Path);

            _logger.LogInformation("Removed all from DDB");

            // TODO: Maybe it's more clever to remove the entire sqlite database instead of performing a per-file delete. Just my 2 cents
            
        }
    }
}
