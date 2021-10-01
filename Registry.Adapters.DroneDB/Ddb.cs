using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using DDB.Bindings;
using GeoJSON.Net;
using GeoJSON.Net.CoordinateReferenceSystem;
using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.ObjectSystem;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using Point = GeoJSON.Net.Geometry.Point;
using Polygon = GeoJSON.Net.Geometry.Polygon;

namespace Registry.Adapters.DroneDB
{
    public class Ddb : IDdb
    {

        [JsonConstructor]
        private Ddb()
        {
            //
        }

        [OnDeserialized]
        internal void OnDeserializedMethod(StreamingContext context)
        {
            BuildFolderPath = Path.Combine(DatasetFolderPath, DatabaseFolderName, BuildFolderName);
            Meta = new DdbMetaManager(this);
        }

        public Ddb(string ddbPath)
        {
            if (string.IsNullOrWhiteSpace(ddbPath))
                throw new ArgumentException("Path should not be null or empty");

            if (!Directory.Exists(ddbPath))
                throw new ArgumentException($"Path '{ddbPath}' does not exist");

            DatasetFolderPath = ddbPath;
            BuildFolderPath = Path.Combine(ddbPath, DatabaseFolderName, BuildFolderName);
            Meta = new DdbMetaManager(this);
        }

        public string GenerateTile(string imagePath, int tz, int tx, int ty, bool retina, bool tms)
        {
            try
            {
                return DDB.Bindings.DroneDB.GenerateTile(imagePath, tz, tx, ty, retina ? 512 : 256, true);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot generate tile of '{imagePath}'", ex);
            }
        }

        public void Init()
        {
            try
            {
                var res = DDB.Bindings.DroneDB.Init(DatasetFolderPath);
                Debug.WriteLine(res);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot initialize ddb in folder '{DatasetFolderPath}'", ex);
            }
        }

        // These consts are like magic strings: if anything changes this goes kaboom!
        public const string DatabaseFolderName = ".ddb";
        public const string BuildFolderName = "build";
        
        string IDdb.DatabaseFolderName => DatabaseFolderName;
        string IDdb.BuildFolderName => BuildFolderName;

        [JsonIgnore]
        public string Version => DDB.Bindings.DroneDB.GetVersion();

        [JsonProperty]
        public string DatasetFolderPath { get; private set; }
        
        [JsonProperty]
        public string BuildFolderPath { get; private set; }

        [JsonIgnore]
        public IDdbMetaManager Meta { get; private set; }

        public long GetSize()
        {
            return DDB.Bindings.DroneDB.Info(DatasetFolderPath).FirstOrDefault()?.Size ?? 0;
        }

        static Ddb()
        {
#if DEBUG
            DDB.Bindings.DroneDB.RegisterProcess(true);
#else
            DDB.Bindings.DroneDB.RegisterProcess(false);
#endif
        }

        public DdbEntry GetEntry(string path)
        {
            var objs = Search(path, true).ToArray();

            if (objs.Any(p => p.Path == path))
                return objs.First();

            var parent = Path.GetDirectoryName(path);

            if (string.IsNullOrEmpty(parent)) parent = "*";

            objs = Search(parent, true).ToArray();

            return objs.FirstOrDefault(item => item.Path == path);
        }

        public bool EntryExists(string path)
        {
            return GetEntry(path) != null;
        }


        public IEnumerable<DdbEntry> Search(string path, bool recursive = false)
        {
            try
            {
                // If the path is not absolute let's rebase it on ddbPath
                if (path != null && !Path.IsPathRooted(path))
                    path = Path.Combine(DatasetFolderPath, path);

                // If path is null we use the base ddb path
                path ??= DatasetFolderPath;

                var entries = DDB.Bindings.DroneDB.List(DatasetFolderPath, path, recursive);

                if (entries == null)
                {
                    Debug.WriteLine("Strange null return value");
                    return Array.Empty<DdbEntry>();
                }

                var query = from entry in entries
                    select new DdbEntry
                    {
                        Depth = entry.Depth,
                        Hash = entry.Hash,
                        Properties = entry.Properties,
                        ModifiedTime = entry.ModifiedTime,
                        Path = entry.Path,
                        Size = entry.Size,
                        Type = (EntryType)(int)entry.Type,

                        PointGeometry = (Point)entry.PointGeometry?.ToObject<Feature>()?.Geometry,
                        PolygonGeometry = (Polygon)entry.PolygonGeometry?.ToObject<Feature>()?.Geometry
                    };


                return query.ToArray();
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

                DDB.Bindings.DroneDB.Remove(DatasetFolderPath, path);
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
                DDB.Bindings.DroneDB.MoveEntry(DatasetFolderPath, source, dest);
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
                DDB.Bindings.DroneDB.Build(DatasetFolderPath, path, dest, force);
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
                DDB.Bindings.DroneDB.Build(DatasetFolderPath, null, dest, force);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot build all from ddb '{DatasetFolderPath}'", ex);
            }
        }

        public bool IsBuildable(string path)
        {
            try
            {
                return DDB.Bindings.DroneDB.IsBuildable(DatasetFolderPath, path);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot call IsBuildable from ddb '{DatasetFolderPath}'", ex);
            }
        }

        public Dictionary<string, object> GetAttributesRaw()
        {
            return ChangeAttributesRaw(new Dictionary<string, object>());
        }

        public DdbAttributes GetAttributes()
        {
            return new(this);
        }

        public DdbEntry GetInfo()
        {
            return GetInfo(DatasetFolderPath);
        }

        public DdbEntry GetInfo(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty");

            var info = DDB.Bindings.DroneDB.Info(DatasetFolderPath);

            var entry = info.FirstOrDefault();

            if (entry == null)
                throw new InvalidOperationException("Cannot get ddb info of dataset");

            return new DdbEntry
            {
                Depth = entry.Depth,
                Hash = entry.Hash,
                Properties = entry.Properties,
                ModifiedTime = entry.ModifiedTime,
                Path = entry.Path,
                Size = entry.Size,
                Type = (EntryType)(int)entry.Type,

                PointGeometry = (Point)entry.PointGeometry?.ToObject<Feature>()?.Geometry,
                PolygonGeometry = (Polygon)entry.PolygonGeometry?.ToObject<Feature>()?.Geometry
            };
        }

        public Dictionary<string, object> ChangeAttributesRaw(Dictionary<string, object> attributes)
        {
            try
            {
                return DDB.Bindings.DroneDB.ChangeAttributes(DatasetFolderPath, attributes);
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
                return DDB.Bindings.DroneDB.GenerateThumbnail(imagePath, size);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException(
                    $"Cannot generate thumbnail of '{imagePath}' with size '{size}'", ex);
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

                    DDB.Bindings.DroneDB.Add(DatasetFolderPath, folderPath);
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

                    stream.Reset();

                    EnsureFolderExists(filePath);

                    using (var writer = File.OpenWrite(filePath))
                    {
                        stream.CopyTo(writer);
                    }

                    DDB.Bindings.DroneDB.Add(DatasetFolderPath, filePath);
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

        private static void EnsureFolderExists(string filePath)
        {
            var folder = Path.GetDirectoryName(filePath);
            if (folder != null) Directory.CreateDirectory(folder);
        }

        public override string ToString()
        {
            return DatasetFolderPath;
        }
    }
}