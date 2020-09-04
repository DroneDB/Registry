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
using Microsoft.Extensions.Logging;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Implementation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB.Models;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using SQLitePCL;
using LineString = GeoJSON.Net.Geometry.LineString;
using Point = GeoJSON.Net.Geometry.Point;
using Polygon = GeoJSON.Net.Geometry.Polygon;

namespace Registry.Adapters.DroneDB
{
    public class DdbStorage : DbContext, IDdbStorage
    {
        private readonly string _dbPath;
        private readonly IDdb _ddb;

        private const int Srid = 4326;

        public DdbStorage(string dbPath, IDdb ddb)
        {

            if (!File.Exists(dbPath))
                throw new IOException("Sqlite database not found");

            _dbPath = dbPath;
            _ddb = ddb;
        }

        public IEnumerable<DdbEntry> Search(string path)
        {

            var tmp = from entry in Entries
                      select entry;

            // Filter only if necessary
            if (!string.IsNullOrEmpty(path))
                tmp = from item in tmp
                      where item.Path.StartsWith(path)
                      select item;

            var query = from item in tmp.ToArray()
                        select new DdbEntry
                        {
                            Depth = item.Depth,
                            Hash = item.Hash,
                            Meta = JsonConvert.DeserializeObject<JObject>(item.Meta),
                            ModifiedTime = item.ModifiedTime,
                            Path = item.Path,
                            Size = item.Size,
                            Type = item.Type,
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

            var lst = _ddb.Info(tempFile).ToArray();

            File.Delete(tempFile);

            if (lst == null || lst.Length == 0)
                throw new InvalidOperationException("Cannot parse ddb output");

            var obj = lst.First();

            entry = new Entry
            {
                Depth = path.Count(item => item == '/'),
                Meta = obj.Meta.ToString(),
                ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(obj.ModifiedTime).DateTime.ToLocalTime(),
                Path = path,
                Size = obj.Size,
                Type = (EntryType)(int)obj.Type,
                Hash = CommonUtils.ComputeSha256Hash(data)
            };

            var factory = NtsGeometryServices.Instance.CreateGeometryFactory(Srid);

            var pointGeometry = obj.PointGeometry?.Geometry as Point;

            if (pointGeometry != null)
            {
                entry.PointGeometry = factory.CreatePoint(new CoordinateArraySequence(new Coordinate[]
                {
                new CoordinateZ(
                    pointGeometry.Coordinates.Latitude,
                    pointGeometry.Coordinates.Longitude,
                    pointGeometry.Coordinates.Altitude ?? 0)
                }, 3, 0));

            }

            var polygonGeometry = obj.PolygonGeometry?.Geometry;

            if (polygonGeometry != null)
            {
                var polygon = polygonGeometry as Polygon;

                if (polygon == null)
                    throw new InvalidOperationException("Expected polygon_geometry to be a Polygon");

                var set = polygon.Coordinates.FirstOrDefault();

                if (set == null)
                    throw new InvalidOperationException("Expected polygon_geometry to be specified");

                var coords = set.Coordinates.Select(item =>
                        new CoordinateZ(item.Latitude, item.Longitude, item.Altitude ?? 0))
                    .Cast<Coordinate>().ToArray();

                entry.PolygonGeometry = factory.CreatePolygon(new LinearRing(coords));


            }

            RawAddEntry(entry);

            SaveChanges();
        }


        private void RawAddEntry(Entry entry)
        {
            FormattableString query =
                $@"INSERT INTO entries (path, hash, type, meta, mtime, size, depth, point_geom, polygon_geom) VALUES 
                ({entry.Path}, {entry.Hash}, {(int)entry.Type}, {entry.Meta}, 
                {new DateTimeOffset(entry.ModifiedTime).ToUnixTimeSeconds()}, {entry.Size}, {entry.Depth}, 
                GeomFromText({GetWkt(entry.PointGeometry)}, 4326), GeomFromText({GetWkt(entry.PolygonGeometry)}, 4326))";

            var res = Database.ExecuteSqlInterpolated(query);
        }

        private string GetWkt(NetTopologySuite.Geometries.Point point)
        {
            return point == null ?
                string.Empty :
                $"POINT Z ({point.X:F13} {point.Y:F13} {point.Z:F13})";
        }

        private string GetWkt(NetTopologySuite.Geometries.Polygon polygon)
        {
            return polygon == null ?
                string.Empty :
                $"POLYGONZ (( {string.Join(", ", polygon.Coordinates.Select(item => $"{item.X:F13} {item.Y:F13} {item.Z:F13}"))} ))";
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
                CRS = new NamedCRS("EPSG:" + Srid)
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
                CRS = new NamedCRS("EPSG:" + Srid)
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
                .HasSrid(Srid).HasGeometricDimension(Ordinates.XYZ); ;
            modelBuilder.Entity<Entry>().Property(c => c.PolygonGeometry)
                .HasSrid(Srid).HasGeometricDimension(Ordinates.XYZ);

        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {

            optionsBuilder.UseSqlite($"Data Source={_dbPath};Mode=ReadWriteCreate",
                    z => z.UseNetTopologySuite())
#if DEBUG
                .UseLoggerFactory(LoggerFactory.Create(builder => builder.AddConsole()));

            optionsBuilder.EnableSensitiveDataLogging()

#endif
                
                ;
        }

        public DbSet<Entry> Entries { get; set; }


    }
}
