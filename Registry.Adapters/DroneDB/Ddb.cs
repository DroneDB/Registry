using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Adapters.DroneDB
{
    public class DDB : IDDB
    {
        [JsonConstructor]
        private DDB()
        {
            //
        }

        [OnDeserialized]
        internal void OnDeserializedMethod(StreamingContext context)
        {
            BuildFolderPath = Path.Combine(DatasetFolderPath, DatabaseFolderName, BuildFolderName);
            Meta = new MetaManager(this);
        }

        public DDB(string ddbPath)
        {
            if (string.IsNullOrWhiteSpace(ddbPath))
                throw new ArgumentException("Path should not be null or empty");

            if (!Directory.Exists(ddbPath))
                throw new ArgumentException($"Path '{ddbPath}' does not exist");

            DatasetFolderPath = ddbPath;
            BuildFolderPath = Path.Combine(ddbPath, DatabaseFolderName, BuildFolderName);
            Meta = new MetaManager(this);
        }

        public byte[] GenerateTile(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash)
        {
            try
            {
                return DDBWrapper.GenerateMemoryTile(inputPath, tz, tx, ty, retina ? 512 : 256, true, false,
                    inputPathHash);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot generate tile of '{inputPath}'", ex);
            }
        }

        public void Init()
        {
            try
            {
                var res = DDBWrapper.Init(DatasetFolderPath);
                Debug.WriteLine(res);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot initialize ddb in folder '{DatasetFolderPath}'", ex);
            }
        }

        // These consts are like magic strings: if anything changes this goes kaboom!
        public static string DatabaseFolderName = ".ddb";
        public static string BuildFolderName = "build";
        public static string TmpFolderName = "tmp";

        [JsonIgnore] public string Version => DDBWrapper.GetVersion();

        [JsonProperty] public string DatasetFolderPath { get; private set; }

        [JsonProperty] public string BuildFolderPath { get; private set; }

        [JsonIgnore] public IMetaManager Meta { get; private set; }

        public long GetSize()
        {
            return DDBWrapper.Info(DatasetFolderPath).FirstOrDefault()?.Size ?? 0;
        }

        static DDB()
        {
#if DEBUG
            DDBWrapper.RegisterProcess(true);
#else
            DDBWrapper.RegisterProcess(false);
#endif
        }

        public string GetLocalPath(string path)
        {
            return CommonUtils.SafeCombine(DatasetFolderPath, path);
        }

        public Entry GetEntry(string path)
        {
            var objs = Search(path, true).ToArray();

            if (objs.Any(p => p.Path == path))
                return objs.First();

            var parent = Path.GetDirectoryName(path);

            objs = Search(parent).ToArray();

            return objs.FirstOrDefault(item => item.Path == path);
        }

        public bool EntryExists(string path)
        {
            return GetEntry(path) != null;
        }


        public IEnumerable<Entry> Search(string path, bool recursive = false)
        {
            try
            {
                // If the path is not absolute let's rebase it on ddbPath
                if (path != null && !Path.IsPathRooted(path))
                    path = Path.Combine(DatasetFolderPath, path);

                // If path is null we use the base ddb path
                path ??= DatasetFolderPath;

                var entries = DDBWrapper.List(DatasetFolderPath, path, recursive);

                if (entries == null)
                {
                    Debug.WriteLine("Strange null return value");
                    return Array.Empty<Entry>();
                }

                return entries;
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot list '{path}' to ddb '{DatasetFolderPath}'", ex);
            }
        }

        public void Add(string path, byte[] data)
        {
            using var stream = new MemoryStream(data);
            Add(path, stream);
        }

        public void Remove(string path)
        {
            try
            {
                // If the path is not absolute let's rebase it on ddbPath
                if (!Path.IsPathRooted(path)) path = Path.Combine(DatasetFolderPath, path);

                DDBWrapper.Remove(DatasetFolderPath, path);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot remove '{path}' from ddb '{DatasetFolderPath}'", ex);
            }
        }

        public void Move(string source, string dest)
        {
            try
            {
                DDBWrapper.MoveEntry(DatasetFolderPath, source, dest);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot move '{source}' to {dest} from ddb '{DatasetFolderPath}'",
                    ex);
            }
        }

        public void Build(string path, string dest = null, bool force = false)
        {
            try
            {
                DDBWrapper.Build(DatasetFolderPath, path, dest, force);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot build '{path}' from ddb '{DatasetFolderPath}'", ex);
            }
        }

        public void BuildAll(string dest = null, bool force = false)
        {
            try
            {
                DDBWrapper.Build(DatasetFolderPath, null, dest, force);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot build all from ddb '{DatasetFolderPath}'", ex);
            }
        }

        public void BuildPending(string dest = null, bool force = false)
        {
            try
            {
                DDBWrapper.Build(DatasetFolderPath, null, dest, force, true);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot build pending from ddb '{DatasetFolderPath}'", ex);
            }
        }

        public string GetTmpFolder(string path)
        {
            string fullPath = Path.Combine(DatasetFolderPath, DatabaseFolderName, TmpFolderName, path);
            if (!Directory.Exists(fullPath)) Directory.CreateDirectory(fullPath);
            return fullPath;
        }

        public bool IsBuildable(string path)
        {
            try
            {
                return DDBWrapper.IsBuildable(DatasetFolderPath, path);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot call IsBuildable from ddb '{DatasetFolderPath}'", ex);
            }
        }

        public bool IsBuildPending()
        {
            try
            {
                return DDBWrapper.IsBuildPending(DatasetFolderPath);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot call IsBuildPending from ddb '{DatasetFolderPath}'", ex);
            }
        }

        public Dictionary<string, object> GetAttributesRaw()
        {
            return ChangeAttributesRaw(new Dictionary<string, object>());
        }

        public EntryAttributes GetAttributes()
        {
            return new(this);
        }

        public Entry GetInfo()
        {
            return GetInfo(DatasetFolderPath);
        }

        public Entry GetInfo(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty");

            var info = DDBWrapper.Info(DatasetFolderPath);

            var entry = info.FirstOrDefault();

            if (entry == null)
                throw new InvalidOperationException("Cannot get ddb info of dataset");

            return entry;
        }

        public Dictionary<string, object> ChangeAttributesRaw(Dictionary<string, object> attributes)
        {
            try
            {
                return DDBWrapper.ChangeAttributes(DatasetFolderPath, attributes);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot change attributes of ddb '{DatasetFolderPath}'", ex);
            }
        }

        public byte[] GenerateThumbnail(string imagePath, int size)
        {
            try
            {
                return DDBWrapper.GenerateThumbnail(imagePath, size);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException(
                    $"Cannot generate thumbnail of '{imagePath}' with size '{size}'", ex);
            }
        }

        public void AddRaw(string path)
        {
            try
            {
                DDBWrapper.Add(DatasetFolderPath, path);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot add '{path}' to ddb '{DatasetFolderPath}'", ex);
            }
        }

        public void Add(string path, Stream stream = null)
        {
            if (stream == null)
            {
                string folderPath = null;

                try
                {
                    folderPath = Path.Combine(DatasetFolderPath, path);

                    Directory.CreateDirectory(folderPath);

                    DDBWrapper.Add(DatasetFolderPath, folderPath);
                }
                catch (DDBException ex)
                {
                    throw new InvalidOperationException($"Cannot add folder '{path}' to ddb '{DatasetFolderPath}'", ex);
                }
                finally
                {
                    if (folderPath != null && Directory.Exists(folderPath))
                        if (!CommonUtils.SafeDeleteFolder(folderPath))
                            Debug.WriteLine($"Cannot delete folder '{folderPath}'");
                }
            }
            else
            {
                string filePath = null;

                try
                {
                    filePath = Path.Combine(DatasetFolderPath, path);

                    stream.SafeReset();

                    CommonUtils.EnsureSafePath(filePath);

                    using (var writer = File.OpenWrite(filePath))
                    {
                        stream.CopyTo(writer);
                    }

                    DDBWrapper.Add(DatasetFolderPath, filePath);
                }
                catch (DDBException ex)
                {
                    throw new InvalidOperationException($"Cannot add '{path}' to ddb '{DatasetFolderPath}'", ex);
                }
                finally
                {
                    if (filePath != null && File.Exists(filePath))
                        if (!CommonUtils.SafeDelete(filePath))
                            Debug.WriteLine($"Cannot delete file '{filePath}'");
                }
            }
        }

        public override string ToString()
        {
            return DatasetFolderPath;
        }

        public Stamp GetStamp()
        {
            return DDBWrapper.GetStamp(DatasetFolderPath);
        }

        #region Async

        public async Task<IEnumerable<Entry>> SearchAsync(string path, bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            return await Task<IEnumerable<Entry>>.Factory.StartNew(() => Search(path, recursive), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task AddAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            await Task.Factory.StartNew(() => Add(path, data), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task AddAsync(string path, Stream data = null, CancellationToken cancellationToken = default)
        {
            await Task.Factory.StartNew(() => Add(path, data), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task RemoveAsync(string path, CancellationToken cancellationToken = default)
        {
            await Task.Factory.StartNew(() => Remove(path), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task MoveAsync(string source, string dest, CancellationToken cancellationToken = default)
        {
            await Task.Factory.StartNew(() => Move(source, dest), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task<Dictionary<string, object>> ChangeAttributesRawAsync(Dictionary<string, object> attributes,
            CancellationToken cancellationToken = default)
        {
            return await Task<Dictionary<string, object>>.Factory.StartNew(() => ChangeAttributesRaw(attributes),
                cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task<byte[]> GenerateThumbnailAsync(string imagePath, int size,
            CancellationToken cancellationToken = default)
        {
            return await Task<byte[]>.Factory.StartNew(() => GenerateThumbnail(imagePath, size), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task<byte[]> GenerateTileAsync(string inputPath, int tz, int tx, int ty, bool retina,
            string inputPathHash,
            CancellationToken cancellationToken = default)
        {
            return await Task<byte[]>.Factory.StartNew(() => GenerateTile(inputPath, tz, tx, ty, retina, inputPathHash),
                cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task InitAsync(CancellationToken cancellationToken = default)
        {
            await Task.Factory.StartNew(Init, cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task<Dictionary<string, object>> GetAttributesRawAsync(
            CancellationToken cancellationToken = default)
        {
            return await Task<Dictionary<string, object>>.Factory.StartNew(GetAttributesRaw, cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task<EntryAttributes> GetAttributesAsync(CancellationToken cancellationToken = default)
        {
            return await Task<EntryAttributes>.Factory.StartNew(GetAttributes, cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public virtual async Task<Entry> GetInfoAsync(CancellationToken cancellationToken = default)
        {
            return await Task<Entry>.Factory.StartNew(GetInfo, cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task<Entry> GetInfoAsync(string path, CancellationToken cancellationToken = default)
        {
            return await Task<Entry>.Factory.StartNew(() => GetInfo(path), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task<Entry> GetEntryAsync(string path, CancellationToken cancellationToken = default)
        {
            return await Task<Entry>.Factory.StartNew(() => GetEntry(path), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task<bool> EntryExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            return await Task<bool>.Factory.StartNew(() => EntryExists(path), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task BuildAsync(string path, string dest = null, bool force = false,
            CancellationToken cancellationToken = default)
        {
            await Task.Factory.StartNew(() => Build(path, dest, force), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task BuildAllAsync(string dest = null, bool force = false,
            CancellationToken cancellationToken = default)
        {
            await Task.Factory.StartNew(() => BuildAll(dest, force), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task<bool> IsBuildableAsync(string path, CancellationToken cancellationToken = default)
        {
            return await Task<bool>.Factory.StartNew(() => IsBuildable(path), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task<bool> IsBuildPendingAsync(CancellationToken cancellationToken = default)
        {
            return await Task<bool>.Factory.StartNew(() => IsBuildPending(), cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
        {
            return await Task<long>.Factory.StartNew(GetSize, cancellationToken,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        #endregion
    }
}