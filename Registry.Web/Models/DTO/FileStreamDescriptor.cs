using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity.UI.V4.Pages.Internal.Account;
using Microsoft.Extensions.Logging;
using MimeMapping;
using Registry.Common;
using Registry.Ports.ObjectSystem;
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Models.DTO
{

    public enum FileDescriptorType
    {
        Single, Multiple, Dataset
    }

    public class FileStreamDescriptor
    {
        private readonly string _orgSlug;
        private readonly Guid _internalRef;
        private readonly string[] _paths;
        private readonly string[] _folders;
        private readonly FileDescriptorType _descriptorType;
        private readonly IObjectSystem _objectSystem;
        private readonly IObjectsManager _objectManager;
        private readonly ILogger<ObjectsManager> _logger;
        private readonly IDdbManager _ddbManager;
        private readonly IUtils _utils;

        public string Name { get; }

        public string ContentType { get; }

        public FileStreamDescriptor(string name, string contentType, string orgSlug, Guid internalRef, string[] paths, string[] folders,
            FileDescriptorType descriptorType, IObjectSystem objectSystem, IObjectsManager objectManager, ILogger<ObjectsManager> logger, IDdbManager ddbManager, IUtils utils)
        {
            _orgSlug = orgSlug;
            _internalRef = internalRef;
            _paths = paths;
            _folders = folders;
            _descriptorType = descriptorType;
            _objectSystem = objectSystem;
            _objectManager = objectManager;
            _logger = logger;
            _ddbManager = ddbManager;
            _utils = utils;
            Name = name;
            ContentType = contentType;
        }

        public async Task CopyToAsync(Stream stream)
        {
            // If there is just one file we return it
            if (_descriptorType == FileDescriptorType.Single)
            {
                var filePath = _paths.First();

                _logger.LogInformation($"Only one path found: '{filePath}'");

                await WriteObjectContentStream(_orgSlug, _internalRef, filePath, stream);

            }
            // Otherwise we zip everything together and return the package
            else
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
                foreach (var path in _paths)
                {
                    _logger.LogInformation($"Zipping: '{path}'");

                    var entry = archive.CreateEntry(path, CommonUtils.GetCompressionLevel(path));
                    await using var entryStream = entry.Open();

                    await WriteObjectContentStream(_orgSlug, _internalRef, path, entryStream);
                }

                // We treat folders separately because if they are empty they would not be included in the archive
                if (_folders != null)
                {
                    foreach (var folder in _folders)
                        archive.CreateEntry(folder + "/");
                }

                // Include ddb folder
                if (_descriptorType == FileDescriptorType.Dataset)
                {
                    var ddb = _ddbManager.Get(_orgSlug, _internalRef);

                    archive.CreateEntryFromAny(Path.Combine(ddb.DatabaseFolder, _ddbManager.DdbFolderName), string.Empty, new[] { ddb.BuildFolder });
                }

            }

        }

        private async Task WriteObjectContentStream(string orgSlug, Guid internalRef, string path, Stream stream)
        {
            var bucketName = _utils.GetBucketName(orgSlug, internalRef);

            _logger.LogInformation($"Using bucket '{bucketName}'");

            var bucketExists = await _objectSystem.BucketExistsAsync(bucketName);

            if (!bucketExists)
                throw new NotFoundException($"Cannot find bucket '{bucketName}'");

            var objInfo = await _objectSystem.GetObjectInfoAsync(bucketName, path);

            if (objInfo == null)
                throw new NotFoundException($"Cannot find '{path}' in storage provider");

            _logger.LogInformation($"Getting object '{path}' in bucket '{bucketName}'");

            await _objectSystem.GetObjectAsync(bucketName, path, s => s.CopyTo(stream));

        }
    }
}