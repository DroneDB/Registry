using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Registry.Common;

public static class EfCoreExtenders
{
    /// <summary>
    /// This methos is used to perform a safe database migration. When the migrations table does not exist it creates it and assumes the database is in its initial migration.
    /// </summary>
    /// <param name="databaseFacade"></param>
    /// <param name="cancellationToken"></param>
    public static async Task SafeMigrateAsync(
        this DatabaseFacade databaseFacade,
        CancellationToken cancellationToken = default)
    {
        
        var migrations = databaseFacade.GetMigrations().ToArray();

        if (!migrations.Any())
            return;

        var historyRepository = databaseFacade.GetService<IHistoryRepository>();

        if (await historyRepository.ExistsAsync(cancellationToken))
        {
            
            if ((await databaseFacade.GetPendingMigrationsAsync(cancellationToken: cancellationToken)).Any())
                await databaseFacade.MigrateAsync(cancellationToken);

            return;
        }

        var createScript = historyRepository.GetCreateScript();
        await databaseFacade.ExecuteSqlRawAsync(createScript, cancellationToken);

        var migrationsAssembly = databaseFacade.GetService<IMigrationsAssembly>();

        // Get entity framework core version
        var version = migrationsAssembly.ModelSnapshot?.Model.GetProductVersion() ?? "1.0.0";

        var insertScript = historyRepository.GetInsertScript(new HistoryRow(migrations.First(), version));
        await databaseFacade.ExecuteSqlRawAsync(insertScript, cancellationToken);
    }
}