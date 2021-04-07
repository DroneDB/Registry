using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using DDB.Bindings;
using DDB.Bindings.Model;
using Newtonsoft.Json;
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

        public PushManager(IUtils utils, IDdbManager ddbManager)
        {
            _utils = utils;
            _ddbManager = ddbManager;
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
            await File.WriteAllTextAsync(Path.Combine(baseTempFolder, DeltaFileName), JsonConvert.SerializeObject(delta));

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

        public async Task<object> Commit(string orgSlug, string dsSlug)
        {

            await _utils.GetDataset(orgSlug, dsSlug);

            // Check push folder integrity (ddb, delta and files)
            // Applies delta 
            // Replaces ddb folder
            // Updates last sync

            throw new NotImplementedException();
        }
    }
}
