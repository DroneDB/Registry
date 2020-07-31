using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Models;

namespace Registry.Web.Services.Adapters
{
    public sealed class ApplicationDbContext : IdentityDbContext<User>
    {

        public const string AdminRoleName = "Admin";
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {

        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

        }
        
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);

#if DEBUG
            optionsBuilder.EnableSensitiveDataLogging();
#endif
        }

    }

}
