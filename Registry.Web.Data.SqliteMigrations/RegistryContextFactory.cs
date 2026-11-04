using Microsoft.EntityFrameworkCore.Design;

namespace Registry.Web.Data.SqliteMigrations;

/// <summary>
/// How the EF Core tools build <see cref="RegistryContext"/> for this migrations project (SQLite): found in this
/// assembly, so no startup project, no configuration and no host are needed. <see cref="DesignTimeContexts"/> says where
/// the connection string comes from. Add a migration for this provider with
/// <c>dotnet ef migrations add Name --project Registry.Web.Data.SqliteMigrations</c>.
/// </summary>
public sealed class RegistryContextFactory : IDesignTimeDbContextFactory<RegistryContext>
{
    public RegistryContext CreateDbContext(string[] args) =>
        DesignTimeContexts.Create<RegistryContext>(options => new RegistryContext(options), DbProvider.Sqlite,
            typeof(RegistryContextFactory).Assembly);
}
