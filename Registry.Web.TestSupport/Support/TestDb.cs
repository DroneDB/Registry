#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Registry.Web.Test.Support;

/// <summary>
/// Real relational test databases (SQLite) for the EF contexts (<c>RegistryContext</c>,
/// <c>ApplicationDbContext</c>), so tests see the constraints, transactions and SQL translation
/// production sees. The schema comes from the current model (<c>EnsureCreated</c>).
/// <list type="bullet">
/// <item><see cref="Create{TContext}"/>: private <c>:memory:</c> database on one keep-alive
/// connection that every context of the handle shares. For single-threaded tests.</item>
/// <item><see cref="CreateFile{TContext}"/>: temp-file database in WAL mode, one connection per
/// context (as in production). For tests that use contexts from several threads at once.</item>
/// <item><see cref="CreateContext{TContext}"/>: one context owning its private <c>:memory:</c>
/// database; disposing the context drops the database.</item>
/// </list>
/// </summary>
public static class TestDb
{
    public static TestDb<TContext> Create<TContext>(Action<DbContextOptionsBuilder<TContext>>? configure = null)
        where TContext : DbContext
    {
        var connection = OpenMemoryConnection();
        try
        {
            return new TestDb<TContext>(MemoryOptions(connection, owned: false, configure), connection, file: null);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static TestDb<TContext> CreateFile<TContext>(Action<DbContextOptionsBuilder<TContext>>? configure = null)
        where TContext : DbContext
    {
        var file = Path.Combine(Path.GetTempPath(), $"registry-testdb-{Guid.NewGuid():N}.sqlite");
        // No pooling: once disposed, no connection keeps the file open, so it can be deleted.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = file, Pooling = false, ForeignKeys = true
        }.ToString();
        try
        {
            using (var init = new SqliteConnection(connectionString))
            {
                init.Open();
                using var wal = init.CreateCommand();
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                wal.ExecuteNonQuery();
            }

            var builder = new DbContextOptionsBuilder<TContext>().UseSqlite(connectionString);
            configure?.Invoke(builder);
            return new TestDb<TContext>(builder.Options, keepAlive: null, file);
        }
        catch
        {
            TestDb<TContext>.DeleteDatabaseFiles(file);
            throw;
        }
    }

    /// <summary>
    /// A context on its own <c>:memory:</c> database, which it drops when disposed. <paramref name="seed"/>
    /// runs first on a separate, short-lived context, so the returned context starts with an empty
    /// change tracker (as if the data had been written by another request).
    /// </summary>
    public static TContext CreateContext<TContext>(Action<TContext>? seed = null,
        Action<DbContextOptionsBuilder<TContext>>? configure = null) where TContext : DbContext
    {
        var connection = OpenMemoryConnection();
        try
        {
            using (var schema = TestDb<TContext>.Instantiate(MemoryOptions(connection, owned: false, configure)))
            {
                schema.Database.EnsureCreated();
                seed?.Invoke(schema);
            }

            return TestDb<TContext>.Instantiate(MemoryOptions(connection, owned: true, configure));
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static DbContextOptions<TContext> MemoryOptions<TContext>(SqliteConnection connection, bool owned,
        Action<DbContextOptionsBuilder<TContext>>? configure) where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>().UseSqlite(connection, contextOwnsConnection: owned);
        configure?.Invoke(builder);
        return builder.Options;
    }

    private static SqliteConnection OpenMemoryConnection()
    {
        // A :memory: database lives as long as its connection: keep this one open.
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ":memory:", ForeignKeys = true }.ToString());
        connection.Open();
        return connection;
    }
}

/// <summary>
/// Handle of a test database created by <see cref="TestDb"/>. Disposing it disposes the contexts it
/// created and drops the database.
/// </summary>
public sealed class TestDb<TContext> : IDisposable, IAsyncDisposable where TContext : DbContext
{
    private readonly SqliteConnection? _keepAlive;
    private readonly string? _file;
    private readonly List<TContext> _contexts = [];
    private bool _disposed;

    internal TestDb(DbContextOptions<TContext> options, SqliteConnection? keepAlive, string? file)
    {
        Options = options;
        _keepAlive = keepAlive;
        _file = file;

        using var schema = Instantiate(options);
        schema.Database.EnsureCreated();
    }

    public DbContextOptions<TContext> Options { get; }

    /// <summary>
    /// A new context on this database, disposed with the handle. Thread-safe. File databases give
    /// every context its own connection; <c>:memory:</c> databases share the keep-alive connection,
    /// so their contexts must not be used concurrently.
    /// </summary>
    public TContext NewContext()
    {
        lock (_contexts)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var context = Instantiate(Options);
            _contexts.Add(context);
            return context;
        }
    }

    internal static TContext Instantiate(DbContextOptions<TContext> options) =>
        (TContext)Activator.CreateInstance(typeof(TContext), options)!;

    internal static void DeleteDatabaseFiles(string file)
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm" })
            File.Delete(path);
    }

    public void Dispose()
    {
        lock (_contexts)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var context in _contexts)
                context.Dispose();
            _contexts.Clear();
        }

        _keepAlive?.Dispose();
        if (_file != null)
            DeleteDatabaseFiles(_file);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
