using System;
using System.Collections.Generic;
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

        public Ddb(string dbPath)
        {

            if (!File.Exists(dbPath))
                throw new IOException("Sqlite database not found");

            _dbPath = dbPath;
        }

        public IEnumerable<DdbObject> Search(string path)
        {

            var tmp = from entry in Entries select entry;

            // Filter only if necessary
            if (!string.IsNullOrEmpty(path))
                tmp = from item in tmp
                      where item.Path.EndsWith(path)
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
                            Type = item.Type,
                            PointGeometry = GetPoint(item.PointGeometry),
                            PolygonGeometry = GetFeature(item.PolygonGeometry)
                        };

            return query.ToArray();
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
                entity.ToTable("entries").HasNoKey();
            });

            modelBuilder
                .Entity<Entry>()
                .Property(e => e.ModifiedTime)
                .HasConversion(
                    v => new DateTimeOffset(v).ToUnixTimeSeconds(),
                    v => DateTimeOffset.FromUnixTimeSeconds(v)
                        .DateTime.ToLocalTime());

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
