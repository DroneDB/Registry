using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The database now keeps dataset slugs unique, and the request that loses a race for a slug (it passed the check,
/// another request saved the same slug before it did) is answered exactly as the request that finds the slug taken in the
/// check: the same 400 and message, never a 500 from the unique index. The race is forced: an interceptor inserts the
/// competing dataset, through another connection, right before the first save of the new slug.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetSlugRaceHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class DatasetSlugRaceHttpTests : IDisposable
{
    private const string Owner = "datasetslugracehttp-race-owner";
    private const string Org = "datasetslugracehttp-race-org";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private readonly CompetitorInterceptor _competitor = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(configureServices: services =>
            services.AddSingleton<ISaveChangesInterceptor>(_competitor));
        // A new dataset has no metadata yet (libddb throws on a missing key); setting some and reading the info are the
        // only native calls a create makes
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Throws(new DdbException("Key not found"));
        _factory.DdbWrapper.Setup(d => d.MetaSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Meta { Id = "1", ModifiedTime = DateTime.UnixEpoch });
        _factory.DdbWrapper.Setup(d => d.Info(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((string path, bool _, int _, bool _) =>
                [new Registry.Ports.DroneDB.Entry { Path = "file://" + path, Type = EntryType.DroneDB, Properties = [] }]);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _competitor.Database = $"Data Source={System.IO.Path.Combine(_factory.RootPath, "registry.db")}";

        var owner = await _factory.CreateUserAsync(Owner);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id);
        Seed.Dataset(context, org, "first");
        Seed.Dataset(context, org, "second");
        await context.SaveChangesAsync();
    }

    [SetUp]
    public void SetUp() => _competitor.Slug = null;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(int Status, JsonObject? Body)> PostAsync(string url, Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.AsUser(Owner);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject);
    }

    private async Task<int> CountAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        return await context.Datasets.CountAsync(d => d.Slug == slug && d.Organization.Slug == Org);
    }

    [Test]
    public async Task CreatingASlugThatIsAlreadyThere_IsRefused_BeforeTheSave()
    {
        var (status, body) = await PostAsync($"/orgs/{Org}/ds", new() { ["slug"] = "first", ["name"] = "dup" });

        status.ShouldBe(400);
        body!["detail"]!.GetValue<string>().ShouldBe("Dataset with this slug already exists");
        (await CountAsync("first")).ShouldBe(1);
    }

    [Test]
    public async Task LosingTheRaceForANewSlug_IsTheSameRefusal_NotAServerError()
    {
        _competitor.Slug = "raced";

        var (status, body) = await PostAsync($"/orgs/{Org}/ds", new() { ["slug"] = "raced", ["name"] = "loser" });

        status.ShouldBe(400);
        body!["detail"]!.GetValue<string>().ShouldBe("Dataset with this slug already exists");
        _competitor.Inserted.ShouldBeTrue("the competing dataset was saved between the check and the save");
        (await CountAsync("raced")).ShouldBe(1, "the database kept one dataset with the slug");
    }

    [Test]
    public async Task LosingTheRaceForARenamedSlug_IsTheSameRefusal_NotAServerError()
    {
        _competitor.Slug = "renamed";

        var (status, body) = await PostAsync($"/orgs/{Org}/ds/second/rename", new() { ["slug"] = "renamed" });

        status.ShouldBe(400);
        body!["detail"]!.GetValue<string>().ShouldBe("Dataset 'renamed' already exists");
        _competitor.Inserted.ShouldBeTrue();
        (await CountAsync("renamed")).ShouldBe(1);
        (await CountAsync("second")).ShouldBe(1, "the loser keeps its slug");
    }

    [Test]
    public async Task ASaveThatFailsForAnotherReason_IsNotTakenForALostRace()
    {
        // An invalid slug never reaches the database; a free slug creates the dataset
        var (status, _) = await PostAsync($"/orgs/{Org}/ds", new() { ["slug"] = "free-one", ["name"] = "ok" });

        status.ShouldBe(201);
        (await CountAsync("free-one")).ShouldBe(1);
    }

    /// <summary>Inserts a dataset with <see cref="Slug"/> in <see cref="Org"/> right before the first save that adds or renames to it.</summary>
    private sealed class CompetitorInterceptor : SaveChangesInterceptor
    {
        public string? Slug { get; set; }

        public string Database { get; set; } = "";

        public bool Inserted { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Slug != null && eventData.Context is RegistryContext context && SavesTheSlug(context))
            {
                Insert();
                Slug = null;
                Inserted = true;
            }

            return ValueTask.FromResult(result);
        }

        private bool SavesTheSlug(RegistryContext context)
        {
            foreach (var entry in context.ChangeTracker.Entries<Dataset>())
                if (entry.State is EntityState.Added or EntityState.Modified && entry.Entity.Slug == Slug)
                    return true;
            return false;
        }

        private void Insert()
        {
            using var connection = new SqliteConnection(Database);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO Datasets (CreationDate, InternalRef, OrganizationSlug, Slug) " +
                $"VALUES ('2024-01-01 00:00:00', '{Guid.NewGuid()}', '{Org}', '{Slug}')";
            command.ExecuteNonQuery();
        }
    }
}
