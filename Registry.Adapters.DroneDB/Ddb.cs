using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using DDB.Bindings;
using GeoJSON.Net;
using GeoJSON.Net.CoordinateReferenceSystem;
using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using LineString = GeoJSON.Net.Geometry.LineString;
using Point = GeoJSON.Net.Geometry.Point;
using Polygon = GeoJSON.Net.Geometry.Polygon;

namespace Registry.Adapters.DroneDB
{

    public class Ddb : IDdb
    {

        public Ddb(string ddbPath)
        {

            if (string.IsNullOrWhiteSpace(ddbPath))
                throw new ArgumentException("Path should not be null or empty");

            if (!Directory.Exists(ddbPath))
                throw new ArgumentException($"Path '{ddbPath}' does not exist");

            FolderPath = ddbPath;

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
                var res = DDB.Bindings.DroneDB.Init(FolderPath);
                Debug.WriteLine(res);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot initialize ddb in folder '{FolderPath}'", ex);
            }
        }

        public string Version => DDB.Bindings.DroneDB.GetVersion();
        public string FolderPath { get; }

        static Ddb()
        {
#if DEBUG
            DDB.Bindings.DroneDB.RegisterProcess(true);
#else
            DDB.Bindings.DroneDB.RegisterProcess(false);
#endif
        }

        public IEnumerable<DdbEntry> Search(string path, bool recursive = false)
        {

            try
            {

                // If the path is not absolute let's rebase it on ddbPath
                if (path != null && !Path.IsPathRooted(path))
                    path = Path.Combine(FolderPath, path);

                // If path is null we use the base ddb path
                path ??= FolderPath;
                
                var entries = DDB.Bindings.DroneDB.List(FolderPath, path, recursive);
                
                if (entries == null)
                {
                    Debug.WriteLine("Strange null return value");
                    return new DdbEntry[0];
                }

                var query = from entry in entries
                            select new DdbEntry
                            {
                                Depth = entry.Depth,
                                Hash = entry.Hash,
                                Meta = entry.Meta,
                                ModifiedTime = entry.ModifiedTime,
                                Path = entry.Path,
                                Size = entry.Size,
                                Type = (Common.EntryType)(int)entry.Type,

                                PointGeometry = (Point)entry.PointGeometry?.ToObject<Feature>()?.Geometry,
                                PolygonGeometry = (Polygon)entry.PolygonGeometry?.ToObject<Feature>()?.Geometry
                            };


                return query.ToArray();
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot list '{path}' to ddb '{FolderPath}'", ex);
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
                if (!Path.IsPathRooted(path)) path = Path.Combine(FolderPath, path);

                DDB.Bindings.DroneDB.Remove(FolderPath, path);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot remove '{path}' from ddb '{FolderPath}'", ex);
            }
        }

        public Dictionary<string, object> GetAttributes()
        {
            return ChangeAttributes(new Dictionary<string, object>());
        }

        public Dictionary<string, object> ChangeAttributes(Dictionary<string, object> attributes)
        {
            try
            {

                return DDB.Bindings.DroneDB.ChangeAttributes(FolderPath, attributes);

            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot change attributes of ddb '{FolderPath}'", ex);
            }
        }

        public void GenerateThumbnail(string imagePath, int size, string outputPath)
        {
            try
            {

                DDB.Bindings.DroneDB.GenerateThumbnail(imagePath, size, outputPath);

            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot generate thumbnail of '{imagePath}' to '{outputPath}' with size '{size}'", ex);
            }
        }

        public void Add(string path, Stream stream)
        {
            string filePath = null;

            try
            {

                filePath = Path.Combine(FolderPath, path);

                stream.Reset();

                EnsureFolderExists(filePath);

                using (var writer = File.OpenWrite(filePath))
                {
                    stream.CopyTo(writer);
                }

                DDB.Bindings.DroneDB.Add(FolderPath, filePath);

            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot add '{path}' to ddb '{FolderPath}'", ex);
            }
            finally
            {
                if (filePath != null && File.Exists(filePath)) 
                    if (!CommonUtils.SafeDelete(filePath))
                        Debug.WriteLine($"Cannot delete file '{filePath}'");
            }
        }

        private static void EnsureFolderExists(string filePath)
        {
            var folder = Path.GetDirectoryName(filePath);
            Directory.CreateDirectory(folder);
        }

        public override string ToString()
        {
            return FolderPath;
        }
    }
}
