using Microsoft.EntityFrameworkCore.Design;
using Registry.Web.Data;
using Registry.Web.Identity;

namespace Registry.Web.Identity.SqliteMigrations;

/// <summary>
/// How the EF Core tools build <see cref="ApplicationDbContext"/> for this migrations project (SQLite): found in this
/// assembly, so no startup project, no configuration and no host are needed. <see cref="DesignTimeContexts"/> says where
/// the connection string comes from. Add a migration for this provider with
/// <c>dotnet ef migrations add Name --project Registry.Web.Identity.SqliteMigrations</c>.
/// </summary>
public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args) =>
        DesignTimeContexts.Create<ApplicationDbContext>(options => new ApplicationDbContext(options), DbProvider.Sqlite,
            typeof(ApplicationDbContextFactory).Assembly);
}
