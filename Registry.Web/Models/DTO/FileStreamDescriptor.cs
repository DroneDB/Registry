using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
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
        private readonly ILogger<ObjectsManager> _logger;
        private readonly IDdbManager _ddbManager;
        private readonly IDDB _ddb;

        public string Name { get; }

        public string ContentType { get; }

        public FileStreamDescriptor(string name, string contentType, string orgSlug, Guid internalRef, string[] paths, string[] folders,
            FileDescriptorType descriptorType, ILogger<ObjectsManager> logger, IDdbManager ddbManager)
        {
            _orgSlug = orgSlug;
            _internalRef = internalRef;
            _paths = paths;
            _folders = folders;
            _descriptorType = descriptorType;
            _logger = logger;
            _ddbManager = ddbManager;
            Name = name;
            ContentType = contentType;

            _ddb = ddbManager.Get(orgSlug, internalRef);
        }

        public async Task CopyToAsync(Stream stream)
        {
            // If there is just one file we return it
            if (_descriptorType == FileDescriptorType.Single)
            {
                var filePath = _paths.First();

                _logger.LogInformation("Only one path found: '{FilePath}'", filePath);

                var localPath = _ddb.GetLocalPath(filePath);
                
                await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
                await fileStream.CopyToAsync(stream);

            }
            // Otherwise we zip everything together and return the package
            else
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
                foreach (var path in _paths)
                {
                    _logger.LogInformation("Zipping: '{Path}'", path);

                    var entry = archive.CreateEntry(path, CommonUtils.GetCompressionLevel(path));
                    await using var entryStream = entry.Open();

                    var localPath = _ddb.GetLocalPath(path);

                    await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
                    await fileStream.CopyToAsync(entryStream);

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

                    archive.CreateEntryFromAny(
                        Path.Combine(ddb.DatasetFolderPath, IDDB.DatabaseFolderName), 
                        string.Empty, new[] { ddb.BuildFolderPath });
                }

            }

        }

    }
}