#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-15: the host pools its two contexts (<c>AddDbContextPool</c>, measured in RQ-15's RESULTS: a scope that creates a
/// context and reads one row costs a fifth of the time and of the allocations). A pooled context is handed to the next scope
/// once its scope is over, so what the tests pin is what makes that safe: nothing a scope leaves in the context reaches the
/// next one, the interceptors of the host still see every save, and concurrent scopes never share a context. A context that
/// changes its options in <c>OnConfiguring</c> cannot be pooled at all (EF throws), which is why the options the contexts
/// used to set there are set by the provider configurator.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq15DbContextPoolTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq15DbContextPoolTests
{
    private string _root = null!;
    private CountingInterceptor _interceptor = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("rq15-pool-").FullName;
        _interceptor = new CountingInterceptor();
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private ServiceProvider Host()
    {
        var services = new ServiceCollection().AddSingleton<ISaveChangesInterceptor>(_interceptor);
        services.AddHostDbContext<RegistryContext>(DbProvider.Sqlite, $"Data Source={Path.Combine(_root, "registry.db")}");
        services.AddHostDbContext<ApplicationDbContext>(DbProvider.Sqlite, $"Data Source={Path.Combine(_root, "identity.db")}");
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static T InScope<T>(ServiceProvider host, Func<IServiceProvider, T> use)
    {
        using var scope = host.CreateScope();
        return use(scope.ServiceProvider);
    }

    [Test]
    public void ASequentialScope_GetsTheContextOfThePreviousOne_ResetAndEmpty()
    {
        using var host = Host();
        var first = InScope(host, sp =>
        {
            var context = sp.GetRequiredService<RegistryContext>();
            context.Database.EnsureCreated();
            context.Organizations.Add(new Organization { Slug = "left-behind", Name = "n", CreationDate = DateTime.UtcNow });
            context.Organizations.Local.Count.ShouldBe(1);
            return context;
        });

        var second = InScope(host, sp =>
        {
            var context = sp.GetRequiredService<RegistryContext>();
            context.Organizations.Local.ShouldBeEmpty("what a scope tracked and did not save is not seen by the next");
            context.ChangeTracker.HasChanges().ShouldBeFalse();
            context.ChangeTracker.AutoDetectChangesEnabled.ShouldBeTrue();
            context.ChangeTracker.QueryTrackingBehavior.ShouldBe(QueryTrackingBehavior.TrackAll);
            return context;
        });

        second.ShouldBeSameAs(first, "the context is pooled");
    }

    [Test]
    public void ConcurrentScopes_NeverShareAContext()
    {
        using var host = Host();
        using var one = host.CreateScope();
        using var two = host.CreateScope();

        one.ServiceProvider.GetRequiredService<RegistryContext>()
            .ShouldNotBeSameAs(two.ServiceProvider.GetRequiredService<RegistryContext>());
        one.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .ShouldNotBeSameAs(two.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    [Test]
    public async Task TheInterceptorsOfTheHost_SeeTheSavesOfEveryScope_OfBothContexts()
    {
        using var host = Host();
        using (var scope = host.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RegistryContext>().Database.EnsureCreatedAsync();
        using (var scope = host.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
        _interceptor.Saves = 0;

        for (var i = 0; i < 3; i++)
        {
            using var scope = host.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            registry.Organizations.Add(new Organization { Slug = $"org-{i}", Name = "n", CreationDate = DateTime.UtcNow });
            await registry.SaveChangesAsync();
            var identity = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            identity.Users.Add(new User { UserName = $"user-{i}" });
            await identity.SaveChangesAsync();
        }

        _interceptor.Saves.ShouldBe(6, "the singleton interceptor is part of the pooled options, not lost with the first scope");
        using var check = host.CreateScope();
        (await check.ServiceProvider.GetRequiredService<RegistryContext>().Organizations.CountAsync()).ShouldBe(3);
    }

    [Test]
    public async Task APooledContext_StillReportsDetailedErrors_WhichTheContextsNoLongerSetThemselves()
    {
        using var host = Host();
        using var scope = host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Organizations (Slug, Name, CreationDate, IsPublic) VALUES ('bad', 'n', 'not a date', 1)");

        // With detailed errors the message names the property whose value could not be read
        var error = await Should.ThrowAsync<InvalidOperationException>(() => context.Organizations.ToListAsync());

        error.Message.ShouldContain("Organization.CreationDate");
    }

    private sealed class CountingInterceptor : SaveChangesInterceptor
    {
        public int Saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, System.Threading.CancellationToken cancellationToken = default)
        {
            Saves++;
            return ValueTask.FromResult(result);
        }
    }
}
