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

        public DbSet<Organization> Organizations { get; set; }
        public DbSet<Dataset> Datasets { get; set; }
    }
}
