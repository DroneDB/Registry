using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

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
        if (!await databaseFacade.CanConnectAsync(cancellationToken))
        {
            await databaseFacade.MigrateAsync(cancellationToken);
            return;
        }

        var databaseCreator = databaseFacade.GetService<IRelationalDatabaseCreator>();

        var hasTables = await databaseCreator.HasTablesAsync(cancellationToken);

        var migrations = databaseFacade.GetMigrations().ToArray();

        if (!migrations.Any())
        {
            if (!hasTables)
                await databaseCreator.CreateTablesAsync(cancellationToken);

            return;
        }

        var historyRepository = databaseFacade.GetService<IHistoryRepository>();

        var migrationsTableExists = await historyRepository.ExistsAsync(cancellationToken);

        if (hasTables && !migrationsTableExists)
        {
            await AddBaseMigration(databaseFacade, historyRepository, cancellationToken);
            return;
        }

        if ((await databaseFacade.GetPendingMigrationsAsync(cancellationToken: cancellationToken)).Any())
            await databaseFacade.MigrateAsync(cancellationToken);
    }

    private static async Task AddBaseMigration(DatabaseFacade databaseFacade, IHistoryRepository historyRepository,
        CancellationToken cancellationToken = default)
    {
        var migrations = databaseFacade.GetMigrations().ToArray();
        
        Debug.Assert(migrations.Any());

        var createScript = historyRepository.GetCreateScript();
        await databaseFacade.ExecuteSqlRawAsync(createScript, cancellationToken);

        var insertScript = historyRepository.GetInsertScript(new HistoryRow(migrations.First(), ProductInfo.GetVersion()));
        await databaseFacade.ExecuteSqlRawAsync(insertScript, cancellationToken);
    }
}