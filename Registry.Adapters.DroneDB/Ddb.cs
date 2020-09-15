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
        private readonly string _baseDdbPath;

        public Ddb(string baseDdbPath)
        {

            _baseDdbPath = baseDdbPath;

            Directory.CreateDirectory(baseDdbPath);

            // TODO: It would be nice if we could use the bindings to check this
            if (!Directory.Exists(Path.Combine(baseDdbPath, ".ddb")))
            {
                try
                {
                    var res = DDB.Bindings.DroneDB.Init(baseDdbPath);
                    Debug.WriteLine(res);
                }
                catch (DDBException ex)
                {
                    throw new InvalidOperationException($"Cannot initialize ddb in folder '{baseDdbPath}'", ex);
                }
            }
        }

        public IEnumerable<DdbEntry> Search(string path)
        {

            //var info = DDB.Bindings.DroneDB.Info()

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
            string filePath = null;

            try
            {

                filePath = Path.Combine(_baseDdbPath, path);

                File.WriteAllBytes(filePath, data);

                DDB.Bindings.DroneDB.Add(_baseDdbPath, filePath);

            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot add '{path}' to ddb '{_baseDdbPath}'", ex);
            }
            finally
            {
                if (filePath != null && File.Exists(filePath))
                    File.Delete(filePath);
            }
        }

        public void Remove(string path)
        {
            try
            {
                DDB.Bindings.DroneDB.Remove(_baseDdbPath, path);
            }
            catch (DDBException ex)
            {
                throw new InvalidOperationException($"Cannot remove '{path}' from ddb '{_baseDdbPath}'", ex);
            }
        }
    }
}
