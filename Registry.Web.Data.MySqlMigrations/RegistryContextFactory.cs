using Microsoft.EntityFrameworkCore.Design;

namespace Registry.Web.Data.MySqlMigrations;

/// <summary>
/// How the EF Core tools build <see cref="RegistryContext"/> for this migrations project (MySQL and MariaDB): found in
/// this assembly, so no startup project, no configuration and no host are needed. <see cref="DesignTimeContexts"/> says
/// where the connection string and the server version the model is generated for come from (production runs MariaDB
/// 10.6, the default). Add a migration for this provider with
/// <c>dotnet ef migrations add Name --project Registry.Web.Data.MySqlMigrations</c>.
/// </summary>
public sealed class RegistryContextFactory : IDesignTimeDbContextFactory<RegistryContext>
{
    public RegistryContext CreateDbContext(string[] args) =>
        DesignTimeContexts.Create<RegistryContext>(options => new RegistryContext(options), DbProvider.Mysql,
            typeof(RegistryContextFactory).Assembly);
}
