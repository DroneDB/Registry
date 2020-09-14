using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GeoJSON.Net;
using GeoJSON.Net.CoordinateReferenceSystem;
using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;
using NetTopologySuite;
using NetTopologySuite.Geometries.Implementation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using SQLitePCL;
using LineString = GeoJSON.Net.Geometry.LineString;
using Point = GeoJSON.Net.Geometry.Point;
using Polygon = GeoJSON.Net.Geometry.Polygon;

namespace Registry.Adapters.DroneDB
{

    public class Ddb : IDdb
    {
        private readonly string _dbPath;
        private readonly string _baseDdbPath;

        private const int Srid = 4326;

        public Ddb(string baseDdbPath)
        {

            Directory.CreateDirectory(baseDdbPath);

            // TODO: It would be nice if we could use the bindings to check this
            if (!Directory.Exists(".ddb"))
            {
                var res = DDB.Bindings.DroneDB.Init(baseDdbPath);
            }
            _baseDdbPath = baseDdbPath;
        }

        public IEnumerable<DdbEntry> Search(string path)
        {

            //var res = DDB.Bindings.DroneDB.Info(path)

            return null;
            //using var entities = new DdbContext(_dbPath);

            //var tmp = from entry in entities.Entries
            //    select entry;

            //// Filter only if necessary
            //if (!string.IsNullOrEmpty(path))
            //    tmp = from item in tmp
            //        where item.Path.StartsWith(path)
            //        select item;

            //var query = from item in tmp.ToArray()
            //    select new DdbEntry
            //    {
            //        Depth = item.Depth,
            //        Hash = item.Hash,
            //        Meta = JsonConvert.DeserializeObject<JObject>(item.Meta),
            //        ModifiedTime = item.ModifiedTime,
            //        Path = item.Path,
            //        Size = item.Size,
            //        Type = item.Type,
            //        PointGeometry = GetPoint(item.PointGeometry),
            //        PolygonGeometry = GetFeature(item.PolygonGeometry)
            //    };

            //return query.ToArray();
        }

        public void Add(string path, byte[] data)
        {
            using var entities = new DdbContext(_dbPath);

            var entry = entities.Entries.FirstOrDefault(item => item.Path == path);

            if (entry != null)
                throw new InvalidOperationException($"Entry with path '{path}' already existing in database");

            var fileName = Path.GetFileName(path);
            var tempFile = Path.Combine(_baseDdbPath, fileName);

            File.WriteAllBytes(tempFile, data);

            DDB.Bindings.DroneDB.Add(_dbPath, tempFile);

            //entry = Entries.FirstOrDefault(item => item.Path == tempFile);

            //if (entry == null) 
            //    throw new InvalidOperationException($"Added temp entry '{tempFile}' using ddb bindings but I cannot find it");

            //entry.Path = path;
            //SaveChanges();

            File.Delete(tempFile);

        }

        public void Remove(string path)
        {
            DDB.Bindings.DroneDB.Remove(_dbPath, path);
        }
    }
}
