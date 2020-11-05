using System;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data.Models;

namespace Registry.Web.Data
{
    public class RegistryContext: DbContext
    {
        public RegistryContext(DbContextOptions<RegistryContext> options)
            : base(options)
        {

        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Dataset>()
                .HasIndex(ds => ds.Slug);
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);

#if DEBUG
            optionsBuilder.EnableSensitiveDataLogging();
#endif
        }

        public DbSet<Organization> Organizations { get; set; }
        public DbSet<Dataset> Datasets { get; set; }

        public DbSet<Batch> Batches { get; set; }
        public DbSet<Entry> Entries { get; set; }

        public DbSet<UploadSession> UploadSessions { get; set; }
        public DbSet<FileChunk> FileChunks { get; set; }

    }
}
