#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Shares;
using Registry.Web.Test.Support;
using Shouldly;
using DataEntry = Registry.Web.Data.Models.Entry;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <c>IsBatchReadyResult</c> carries the facts of a batch (ready, the paths it holds), not the tracked EF
/// entity with its entries, and <c>IsPathAllowedAsync</c> reads them.
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class BatchReadinessTests
{
    private const string Token = "batchreadiness-token";
    private const string UserName = "batch-user";

    private TestDb<RegistryContext> _db = null!;
    private Mock<IAuthManager> _auth = null!;
    private ShareBatchStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _db = TestDb.Create<RegistryContext>();
        using (var context = _db.NewContext())
        {
            var org = Seed.Organization(context, "batchreadiness-org");
            var dataset = Seed.Dataset(context, org, "batchreadiness-ds");
            context.Batches.Add(new Batch
            {
                Token = Token, Dataset = dataset, UserName = UserName, Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Status = BatchStatus.Running,
                Entries = [new DataEntry { Path = "a.txt", Hash = "h", Type = EntryType.Generic, Size = 1 }]
            });
            context.SaveChanges();
        }

        _auth = new Mock<IAuthManager>();
        _auth.Setup(a => a.SafeGetCurrentUserName()).ReturnsAsync(UserName);
        _auth.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _store = new ShareBatchStore(_db.NewContext(), _auth.Object, Mock.Of<IDatasetAccessResolver>(),
            Mock.Of<IBatchTokenGenerator>(), TimeProvider.System, NullLogger<ShareBatchStore>.Instance);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task IsReady_OfARunningBatchOfTheCaller_NamesItsPaths_WithoutTheEntity()
    {
        var result = await _store.IsReadyAsync(Token);

        result.IsReady.ShouldBeTrue();
        result.EntryPaths.ShouldBe(["a.txt"]);
        typeof(IsBatchReadyResult).GetProperties().Select(p => p.PropertyType.Namespace)
            .ShouldNotContain(typeof(Batch).Namespace, "no EF entity crosses the manager boundary");
    }

    [Test]
    public async Task IsReady_OfAnotherUsersBatch_IsNotReady()
    {
        _auth.Setup(a => a.SafeGetCurrentUserName()).ReturnsAsync("somebody-else");

        var result = await _store.IsReadyAsync(Token);

        result.ShouldBeSameAs(IsBatchReadyResult.NotReady);
        result.EntryPaths.ShouldBeEmpty();
    }

    [TestCase("a.txt", false)]
    [TestCase("b.txt", true)]
    public async Task IsPathAllowed_IsFalseForAPathTheBatchHolds(string path, bool allowed) =>
        (await _store.IsPathAllowedAsync(Token, path)).ShouldBe(allowed);

    [Test]
    public async Task IsPathAllowed_OfAnUnknownToken_IsFalse() =>
        (await _store.IsPathAllowedAsync("nope", "b.txt")).ShouldBeFalse();
}
