using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Registry.Web.Data.Models;

namespace Registry.Web.Data
{
    public class RegistryContext: DbContext
    {
        public RegistryContext(DbContextOptions<RegistryContext> options)
            : base(options)
        {
        }
        
        public RegistryContext() {}

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Dataset>()
                .HasIndex(ds => ds.Slug);

            modelBuilder
                .Entity<Dataset>()
                .HasOne(ds => ds.Organization)
                .WithMany(org => org.Datasets)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder
                .Entity<Batch>()
                .HasOne(e => e.Dataset)
                .WithMany(e => e.Batches)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder
                .Entity<Entry>()
                .HasOne(e => e.Batch)
                .WithMany(e => e.Entries)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            var valueComparer = new ValueComparer<string[]>(
                (c1, c2) => c1.SequenceEqual(c2),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToArray());
            
            modelBuilder.Entity<Dataset>()
                .Property(e => e.FileTypes)
                .HasConversion(
                    v => string.Join(';', v),
                    v => v.Split(';', StringSplitOptions.RemoveEmptyEntries))
                .Metadata
                .SetValueComparer(valueComparer);


        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
#if DEBUG
            optionsBuilder.EnableSensitiveDataLogging();
            optionsBuilder.EnableDetailedErrors();
#endif
        }

        public DbSet<Organization> Organizations { get; set; }
        public DbSet<Dataset> Datasets { get; set; }

        public DbSet<Batch> Batches { get; set; }
        
    }
    
    public class SqliteRegistryContext : RegistryContext
    {
        public SqliteRegistryContext(DbContextOptions<RegistryContext> options) : base(options)
        {
        }
#if DEBUG_EF
        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            // connect to sqlite database
            options.UseSqlite();
        }
#endif
    }
    
    public class MysqlRegistryContext : RegistryContext
    {
        
        private const string DevConnectionString = "Server=localhost;Database=registry;Uid=root;Pwd=root;";
        
        public MysqlRegistryContext(DbContextOptions<RegistryContext> options) : base(options)
        {
        }
    
#if DEBUG_EF
        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            // connect to sqlite database
            options.UseMySql(DevConnectionString, ServerVersion.AutoDetect(DevConnectionString));
        }
#endif
        
    }
}
