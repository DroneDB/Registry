using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using DDB.Bindings;
using DDB.Bindings.Model;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Registry.Adapters.DroneDB;
using Registry.Ports.ObjectSystem;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers
{
    public class PushManager : IPushManager
    {
        private const string PushFolderName = "push";
        private const string DdbTempFolder = "ddb";
        private const string AddsTempFolder = "add";
        private const string DeltaFileName = "delta.json";

        private readonly IUtils _utils;
        private readonly IDdbManager _ddbManager;
        private readonly IObjectsManager _objectsManager;
        private readonly IObjectSystem _objectSystem;
        private readonly ILogger<PushManager> _logger;

        public PushManager(IUtils utils, IDdbManager ddbManager, IObjectSystem objectSystem,
            IObjectsManager objectsManager, ILogger<PushManager> logger)
        {
            _utils = utils;
            _ddbManager = ddbManager;
            _objectSystem = objectSystem;
            _objectsManager = objectsManager;
            _logger = logger;
        }

        public async Task<PushInitResultDto> Init(string orgSlug, string dsSlug, Stream stream)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            // 0) Setup temp folders
            var baseTempFolder = Path.Combine(Path.GetTempPath(), PushFolderName, orgSlug, dsSlug);
            Directory.CreateDirectory(baseTempFolder);

            var ddbTempFolder = Path.Combine(baseTempFolder, DdbTempFolder);
            Directory.CreateDirectory(ddbTempFolder);

            // 1) Unzip stream contents in temp ddb folder
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            archive.ExtractToDirectory(ddbTempFolder);

            // 2) Perform delta with our ddb
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            // TODO Check: This could be a scope violation: using an external library interface and model class
            var delta = DroneDB.Delta(ddb.FolderPath, ddbTempFolder);

            // 3) Save delta json in temp folder
            await File.WriteAllTextAsync(Path.Combine(baseTempFolder, DeltaFileName),
                JsonConvert.SerializeObject(delta));

            // 4) Return missing files list (excluding folders)
            return new PushInitResultDto
            {
                NeededFiles = delta.Adds
                    .Where(item => item.Type != EntryType.Directory)
                    .Select(item => item.Path)
                    .ToArray()
            };
        }

        public async Task Upload(string orgSlug, string dsSlug, string path, Stream stream)
        {
            await _utils.GetDataset(orgSlug, dsSlug);

            if (path.Contains("."))
                throw new InvalidOperationException("Path cannot contain dot notation");

            var baseTempFolder = Path.Combine(Path.GetTempPath(), PushFolderName, orgSlug, dsSlug);

            if (!Directory.Exists(baseTempFolder))
                throw new InvalidOperationException("Cannot upload file before initializing push");

            var addTempFolder = Path.Combine(baseTempFolder, AddsTempFolder);
            Directory.CreateDirectory(addTempFolder);

            // Calculate new file path
            var filePath = Path.Combine(addTempFolder, path);

            // Ensure subfolder exists
            var parentPath = Path.GetDirectoryName(filePath);
            if (parentPath != null) Directory.CreateDirectory(parentPath);

            // Save file in temp folder
            await using var file = File.OpenWrite(addTempFolder);
            await stream.CopyToAsync(file);

        }

        public async Task Commit(string orgSlug, string dsSlug)
        {

            var ds = await _utils.GetDataset(orgSlug, dsSlug);

            var baseTempFolder = Path.Combine(Path.GetTempPath(), PushFolderName, orgSlug, dsSlug);
            var deltaFilePath = Path.Combine(baseTempFolder, DeltaFileName);
            var addTempFolder = Path.Combine(baseTempFolder, AddsTempFolder);
            var ddbTempFolder = Path.Combine(baseTempFolder, DdbTempFolder);

            // Check push folder integrity (delta and files)

            if (!File.Exists(deltaFilePath))
                throw new InvalidOperationException("Delta not found");

            var delta = JsonConvert.DeserializeObject<Delta>(deltaFilePath);

            foreach (var add in delta.Adds)
                if (!File.Exists(Path.Combine(addTempFolder, add.Path)))
                    throw new InvalidOperationException($"Cannot commit: missing '{add.Path}'");

            // Applies delta 
            var bucketName = _objectsManager.GetBucketName(orgSlug, ds.InternalRef);
            await ApplyDelta(bucketName, delta, addTempFolder);

            // Replaces ddb folder
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            Directory.Delete(ddb.FolderPath, true);
            Directory.CreateDirectory(ddb.FolderPath);
            Directory.Move(ddbTempFolder, ddb.FolderPath);

            // Updates last sync
            ddb = _ddbManager.Get(orgSlug, ds.InternalRef);

        }

        private async Task ApplyDelta(string bucketName, Delta delta, string addTempFolder)
        {

            const string tempFolderName = ".tmp";

            _logger.LogInformation("Moving copies to temp folder ");

            for (var index = 0; index < delta.Copies.Length; index++)
            {
                var copy = delta.Copies[index];
                _logger.LogInformation(copy.ToString());

                var source = copy.Source;
                var dest = Path.Combine(tempFolderName, copy.Source);

                EnsureParentFolderExists(dest);

                _logger.LogInformation(
                    $"Changed copy path from {copy.Source} to {Path.Combine(tempFolderName, copy.Source)}");

                await _objectSystem.CopyObjectAsync(bucketName, source, bucketName, dest);

                delta.Copies[index] = new CopyAction(Path.Combine(tempFolderName, copy.Source), copy.Destination);
            }


            _logger.LogInformation("Working on removes");

            foreach (var rem in delta.Removes)
            {
                _logger.LogInformation(rem.ToString());

                var dest = rem.Path;

                if (rem.Type != EntryType.Directory)
                {

                    _logger.LogInformation("Deleting file");

                    if (await _objectSystem.ObjectExistsAsync(bucketName, dest))
                    {
                        _logger.LogInformation("File exists in dest, deleting it");
                        await _objectSystem.RemoveObjectAsync(bucketName, dest);
                        _logger.LogInformation("File deleted");
                    }
                    else
                    {
                        _logger.LogInformation("File does not exist in dest, nothing to do");
                    }
                }
                //else
                //{
                //    if (Directory.Exists(dest))
                //    {
                //        Console.WriteLine("Directory exists in dest, deleting it");

                //        Directory.Delete(dest, true);
                //    }
                //    else
                //    {
                //        Console.WriteLine("Directory does not exist in dest, nothing to do");

                //    }
                //}

            }


            _logger.LogInformation("Working on adds");

            foreach (var add in delta.Adds)
            {

                _logger.LogInformation(add.ToString());

                if (add.Type == EntryType.Directory)
                {
                    _logger.LogInformation("Cant do much on directories");
                    continue;
                }

                var source = Path.Combine(addTempFolder, add.Path);

                _logger.LogInformation("Uploading file");

                await _objectSystem.PutObjectAsync(bucketName, add.Path, source);
                //File.Copy(source, dest, true);

            }

            _logger.LogInformation("Working on direct copies");

            foreach (var copy in delta.Copies)
            {
                _logger.LogInformation(copy.ToString());

                var source = copy.Source;
                var dest = copy.Destination;

                if (await _objectSystem.ObjectExistsAsync(bucketName, dest))
                {
                    _logger.LogInformation("Dest file exists, writing shadow");
                    await _objectSystem.CopyObjectAsync(bucketName, dest, bucketName, dest + ".replace");
                    //File.Copy(source, dest + ".replace");
                }
                else
                {
                    _logger.LogInformation("Dest file does not exist, performing copy");
                    await _objectSystem.CopyObjectAsync(bucketName, dest, bucketName, dest);

                    //File.Copy(source, dest);
                }
            }

            _logger.LogInformation("Working on shadow copies");

            foreach (var copy in delta.Copies)
            {
                var dest = copy.Destination;

                if (await _objectSystem.ObjectExistsAsync(bucketName, dest + ".replace"))
                {
                    _logger.LogInformation(copy.ToString());
                    _logger.LogInformation("Shadow file exists, replacing original one");

                    // Basically a move
                    await _objectSystem.CopyObjectAsync(bucketName, dest + ".replace", bucketName, dest);
                    await _objectSystem.RemoveObjectAsync(bucketName, dest + ".replace");

                    //File.Move(dest + ".replace", dest, true);
                }
            }

        }

        private void EnsureParentFolderExists(string folder)
        {
            var tempFolder = Path.GetDirectoryName(folder);
            if (tempFolder != null) Directory.CreateDirectory(tempFolder);
        }
    }


}