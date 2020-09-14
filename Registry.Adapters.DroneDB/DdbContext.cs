using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Registry.Adapters.DroneDB.Models;

namespace Registry.Adapters.DroneDB
{
    public class DdbContext : DbContext
    {
        private readonly string _dbPath;
        private const int Srid = 4326;

        public DdbContext(string dbPath)
        {

            if (!File.Exists(dbPath))
                throw new IOException("Cannot find sqlite db");
            

            _dbPath = dbPath;
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
                    v => DateTimeOffset.FromUnixTimeSeconds(v).DateTime);

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