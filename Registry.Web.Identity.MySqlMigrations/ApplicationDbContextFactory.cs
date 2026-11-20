using Microsoft.EntityFrameworkCore.Design;
using Registry.Web.Data;

namespace Registry.Web.Identity.MySqlMigrations;

/// <summary>
/// How the EF Core tools build <see cref="ApplicationDbContext"/> for this migrations project (MySQL and MariaDB):
/// found in this assembly, so no startup project, no configuration and no host are needed.
/// <see cref="DesignTimeContexts"/> says where the connection string and the server version the model is generated for
/// come from (production runs MariaDB 10.6, the default). Add a migration for this provider with
/// <c>dotnet ef migrations add Name --project Registry.Web.Identity.MySqlMigrations</c>.
/// </summary>
public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args) =>
        DesignTimeContexts.Create<ApplicationDbContext>(options => new ApplicationDbContext(options), DbProvider.Mysql,
            typeof(ApplicationDbContextFactory).Assembly);
}
