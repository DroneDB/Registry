using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Newtonsoft.Json;
using Registry.Web.Identity.Models;

namespace Registry.Web.Identity
{
    public sealed class ApplicationDbContext : IdentityDbContext<User>
    {
        public const string AdminRoleName = "admin";

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            var valueComparer = new ValueComparer<Dictionary<string, object>>(
                (first, second) =>
                    first.Count == second.Count &&
                    first.All(pair => second.ContainsKey(pair.Key) && second[pair.Key].Equals(pair.Value)),
                x => x.GetHashCode(),
                x => JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(x)));

            // TODO: Need to set value comparer
            builder.Entity<User>()
                .Property(b => b.Metadata)
                .HasConversion(
                    v => JsonConvert.SerializeObject(v),
                    v => JsonConvert.DeserializeObject<Dictionary<string, object>>(v),
                    valueComparer);
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);

#if DEBUG
            optionsBuilder.EnableSensitiveDataLogging();
#else
            optionsBuilder.EnableSensitiveDataLogging(false);
#endif
            
            optionsBuilder.EnableDetailedErrors();

        }
    }
}