using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GeoJSON.Net.Geometry;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB.Models;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using Point = GeoJSON.Net.Geometry.Point;

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
            var tmp = Entries.ToArray();

            var query = from item in (from entry in Entries
                where entry.Path.StartsWith(path)
                        select entry).ToArray()
                select new DdbObject
                {
                    Depth = item.Depth,
                    Hash = item.Hash,
                    Meta = JsonConvert.DeserializeObject<JObject>(item.Meta),
                    ModifiedTime = item.ModifiedTime,
                    Path = item.Path,
                    Size = item.Size,
                    Type = item.Type,

                    // TODO: convert pointgeom e polygongeom from spatial types to geojson
                    // https://github.com/GeoJSON-Net/GeoJSON.Net
                };

            return query.ToArray();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {

            modelBuilder.Entity<Entry>(entity => {
                entity.ToTable("entries").HasNoKey();
            });

            //modelBuilder.Entity<Entry>()
            //    .HasIndex(ds => ds.Depth);
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
