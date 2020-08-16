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
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB.Models;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using SQLitePCL;
using LineString = GeoJSON.Net.Geometry.LineString;
using Point = GeoJSON.Net.Geometry.Point;
using Polygon = GeoJSON.Net.Geometry.Polygon;

namespace Registry.Adapters.DroneDB
{
    public class Ddb : DbContext, IDdb
    {
        private readonly string _dbPath;
        private readonly string _ddbExePath;

        // TODO: Maybe all this "stuff" can be put in the config
        private const string InfoCommand = "info -f json";
        private const int MaxWaitTime = 5000;

        public Ddb(string dbPath, string ddbExePath)
        {

            if (!File.Exists(dbPath))
                throw new IOException("Sqlite database not found");

            _dbPath = dbPath;
            _ddbExePath = ddbExePath;
        }
        
        public IEnumerable<DdbObject> Search(string path)
        {

            var tmp = from entry in Entries
                      select entry;

            // Filter only if necessary
            if (!string.IsNullOrEmpty(path))
                tmp = from item in tmp
                      where item.Path.StartsWith(path)
                      select item;

            var query = from item in tmp.ToArray()
                        select new DdbObject
                        {
                            Depth = item.Depth,
                            Hash = item.Hash,
                            Meta = JsonConvert.DeserializeObject<JObject>(item.Meta),
                            ModifiedTime = item.ModifiedTime,
                            Path = item.Path,
                            Size = item.Size,
                            Type = (DdbObjectType)(int)item.Type,
                            PointGeometry = GetPoint(item.PointGeometry),
                            PolygonGeometry = GetFeature(item.PolygonGeometry)
                        };

            return query.ToArray();
        }

        public void Add(string path, byte[] data)
        {

            var entry = Entries.FirstOrDefault(item => item.Path == path);

            if (entry != null)
                throw new InvalidOperationException($"Entry with path '{path}' already existing in database");

            var fileName = Path.GetFileName(path);
            var tempFile = Path.Combine(Path.GetTempPath(), fileName);

            File.WriteAllBytes(tempFile, data);

            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(_ddbExePath, $"{InfoCommand} \"{tempFile}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };

            p.StartInfo.EnvironmentVariables.Add("PROJ_LIB", Path.GetDirectoryName(Path.GetFullPath(_ddbExePath)));

            Debug.WriteLine("Running command:");
            Debug.WriteLine($"{Path.GetFullPath(_ddbExePath)} {p.StartInfo.Arguments}");

            p.Start();
            
            if (!p.WaitForExit(MaxWaitTime))
                throw new IOException("Tried to start ddb process but it's taking too long to complete");

            var res = p.StandardOutput.ReadToEnd();

            File.Delete(tempFile);

            var lst = JsonConvert.DeserializeObject<DdbInfoDto[]>(res);

            if (lst == null || lst.Length == 0)
                throw new InvalidOperationException("Cannot parse ddb output");

            var obj = lst.First();

            entry = new Entry
            {
                Depth = path.Count(item => item == '/'),
                Meta = obj.Meta.ToString(),
                ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(obj.ModifiedTime).DateTime.ToLocalTime(),
                Path = path,
                PointGeometry = new NetTopologySuite.Geometries.Point(
                    obj.PointGeometry.Coordinates.Latitude, 
                    obj.PointGeometry.Coordinates.Latitude, 
                    obj.PointGeometry.Coordinates.Altitude ?? 0),
                Size = obj.Size,
                Type = (EntryType)(int)obj.Type
            };

            var polygon = (Polygon)obj.PolygonGeometry.Geometry;
            //var coords = polygon.Coordinates[0].Coordinates.Select(item => new Coordinate(item.)).ToArray();

            // TODO: Convert feature/polygon and check if PointGeometry is a Feature
            /*entry.PolygonGeometry = new NetTopologySuite.Geometries.Polygon(
                new LinearRing(coords));*/

            Entries.Add(entry);
            SaveChanges();
        }

        public void Remove(string path)
        {
            var entry = Entries.FirstOrDefault(item => item.Path == path);

            if (entry == null)
                throw new InvalidOperationException($"Cannot find entry with path '{path}'");

            Entries.Remove(entry);
            SaveChanges();
        }

        private Point GetPoint(NetTopologySuite.Geometries.Point point)
        {

            if (point == null) return null;

            var res = new Point(new Position(point.Y, point.X, point.Z))
            {
                // TODO: Is this always the case?
                CRS = new NamedCRS("EPSG:4326")
            };

            return res;
        }

        private Feature GetFeature(NetTopologySuite.Geometries.Polygon poly)
        {

            if (poly == null) return null;

            var polygon = new Polygon(new[]
            {
                new LineString(poly.Coordinates.Select(item => new Position(item.Y, item.X, item.Z)))
            });

            var feature = new Feature(polygon)
            {
                // TODO: Is this always the case?
                CRS = new NamedCRS("EPSG:4326")
            };

            return feature;

        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {

            modelBuilder.Entity<Entry>(entity =>
            {
                // TODO: This is specified here but the underlying DB hasn't got one. Maybe add it?
                entity.ToTable("entries").HasKey(item => item.Path);
            });

            modelBuilder
                .Entity<Entry>()
                .Property(e => e.ModifiedTime)
                .HasConversion(
                    v => new DateTimeOffset(v).ToUnixTimeSeconds(),
                    v => DateTimeOffset.FromUnixTimeSeconds(v)
                        .DateTime.ToLocalTime());

            modelBuilder.Entity<Entry>().Property(item => item.Type)
                .HasConversion<int>();

            // We need to explicitly set these properties
            modelBuilder.Entity<Entry>().Property(c => c.PointGeometry)
                .HasSrid(4326).HasGeometricDimension(Ordinates.XYZ); ;
            modelBuilder.Entity<Entry>().Property(c => c.PolygonGeometry)
                .HasSrid(4326).HasGeometricDimension(Ordinates.XYZ);

        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {

            optionsBuilder.UseSqlite($"Data Source={_dbPath};Mode=ReadWriteCreate",
                    z => z.UseNetTopologySuite());

#if DEBUG
            optionsBuilder.EnableSensitiveDataLogging();
#endif
        }

        public DbSet<Entry> Entries { get; set; }


    }
}
