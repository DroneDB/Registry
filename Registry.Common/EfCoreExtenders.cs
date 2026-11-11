using System.Diagnostics;
using System.Linq;
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
    /// This method is used to perform a safe database migration. When the migrations table does not exist it creates it,
    /// assumes the database is in its initial migration and then applies the pending ones.
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

        if (migrations.Length == 0)
        {
            if (!hasTables)
                await databaseCreator.CreateTablesAsync(cancellationToken);

            return;
        }

        var historyRepository = databaseFacade.GetService<IHistoryRepository>();

        var migrationsTableExists = await historyRepository.ExistsAsync(cancellationToken);

        // A legacy database (tables, no history) is assumed to be at the baseline migration: stamp it,
        // then fall through so the migrations after the baseline are applied in this same run
        if (hasTables && !migrationsTableExists)
            await AddBaseMigration(databaseFacade, historyRepository, cancellationToken);

        if ((await databaseFacade.GetPendingMigrationsAsync(cancellationToken: cancellationToken)).Any())
            await databaseFacade.MigrateAsync(cancellationToken);
    }

    private static async Task AddBaseMigration(DatabaseFacade databaseFacade, IHistoryRepository historyRepository,
        CancellationToken cancellationToken = default)
    {
        var migrations = databaseFacade.GetMigrations().ToArray();
        
        Debug.Assert(migrations.Length > 0);

        var createScript = historyRepository.GetCreateScript();
        await databaseFacade.ExecuteSqlRawAsync(createScript, cancellationToken);

        var insertScript = historyRepository.GetInsertScript(new HistoryRow(migrations.First(), ProductInfo.GetVersion()));
        await databaseFacade.ExecuteSqlRawAsync(insertScript, cancellationToken);
    }
}