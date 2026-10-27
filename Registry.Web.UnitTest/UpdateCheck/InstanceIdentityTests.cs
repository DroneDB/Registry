#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Web.Services.Ports;
using Registry.Web.Services.UpdateCheck;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The identity of a deployment: a UUID created at the first start in <c>.instance-id</c> of the storage folder, shared by
/// every node, and the hash that proves it without revealing it.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~InstanceIdentityTests"
/// </summary>
[TestFixture]
public class InstanceIdentityTests : IDisposable
{
    private TempStorage _storage = null!;
    private FakeTime _time = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = new TempStorage();
        _time = new FakeTime(new DateTime(2026, 10, 25, 8, 30, 0, DateTimeKind.Utc));
    }

    [TearDown]
    public void Dispose()
    {
        _storage?.Dispose();
        GC.SuppressFinalize(this);
    }

    private InstanceIdentityStore NewNode() =>
        new(_storage.Storage, UpdateCheckTestKit.Disk, _time, NullLogger<InstanceIdentityStore>.Instance);

    [Test]
    public void FirstStart_CreatesAUuid_AndTheJsonFile()
    {
        var identity = NewNode().Current;

        Guid.TryParse(identity.Id, out _).ShouldBeTrue();
        identity.InstalledAt.ShouldBe(_time.Now);

        using var json = JsonDocument.Parse(File.ReadAllText(_storage.File(MagicStrings.InstanceIdFileName)));
        json.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["id", "installedAt"]);
        json.RootElement.GetProperty("id").GetString().ShouldBe(identity.Id);
        json.RootElement.GetProperty("installedAt").GetString().ShouldBe("2026-10-25T08:30:00Z");
    }

    [Test]
    public void Restart_KeepsTheIdentity()
    {
        var first = NewNode().Current;
        _time.Advance(TimeSpan.FromDays(3));

        var afterRestart = NewNode().Current;

        afterRestart.ShouldBe(first);
    }

    [Test]
    public void Nodes_ShareTheFile_SoTheDeploymentHasOneIdentity()
    {
        var web = NewNode();
        var processingNode = NewNode();

        processingNode.Current.Id.ShouldBe(web.Current.Id);
        Directory.GetFiles(_storage.Folder).Select(Path.GetFileName).ShouldBe([MagicStrings.InstanceIdFileName]);
    }

    [Test]
    public async Task NodesStartingTogether_AgreeOnTheOneIdentityThatWasCreated()
    {
        var identities = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => NewNode().Current)));

        identities.Select(i => i.Id).Distinct().Count().ShouldBe(1);
        File.ReadAllText(_storage.File(MagicStrings.InstanceIdFileName)).ShouldContain(identities[0].Id);
    }

    [Test]
    public void ADamagedFile_IsReplacedByANewIdentity()
    {
        File.WriteAllText(_storage.File(MagicStrings.InstanceIdFileName), "{ not json");

        var identity = NewNode().Current;

        Guid.TryParse(identity.Id, out _).ShouldBeTrue();
        NewNode().Current.Id.ShouldBe(identity.Id);
    }

    [Test]
    public void AFileWithoutAUuid_IsReplacedByANewIdentity()
    {
        File.WriteAllText(_storage.File(MagicStrings.InstanceIdFileName), """{"id":"not-a-uuid","installedAt":"2026-01-01T00:00:00Z"}""");

        Guid.TryParse(NewNode().Current.Id, out _).ShouldBeTrue();
    }

    [Test]
    public void ADeletedFile_GivesANewIdentity()
    {
        var first = NewNode().Current;
        File.Delete(_storage.File(MagicStrings.InstanceIdFileName));

        NewNode().Current.Id.ShouldNotBe(first.Id);
    }

    [TestCase("a1b2c3d4-5e6f-4a7b-8c9d-0e1f2a3b4c5d")]
    [TestCase("A1B2C3D4-5E6F-4A7B-8C9D-0E1F2A3B4C5D")]
    public void Hash_IsTheLowercaseHexSha256_OfThePrefixAndTheLowercaseId(string id)
    {
        var expected = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("ddb-instance-v1:a1b2c3d4-5e6f-4a7b-8c9d-0e1f2a3b4c5d"))).ToLowerInvariant();

        new InstanceIdentity(id, DateTime.UtcNow).Hash.ShouldBe(expected);
        expected.Length.ShouldBe(64);
    }

    [Test]
    public void Hash_IsNotPartOfTheFile()
    {
        NewNode().Current.ShouldNotBeNull();

        File.ReadAllText(_storage.File(MagicStrings.InstanceIdFileName)).ShouldNotContain("hash", Case.Insensitive);
    }
}

/// <summary><see cref="AtomicFile.CreateOrRead"/>: the exclusive, complete creation the identity file relies on.</summary>
[TestFixture]
public class AtomicFileCreateOrReadTests : IDisposable
{
    private readonly AtomicFile _atomic = new(UpdateCheckTestKit.Disk);
    private TempStorage _storage = null!;

    [SetUp]
    public void SetUp() => _storage = new TempStorage();

    [TearDown]
    public void Dispose()
    {
        _storage?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Test]
    public void CreatesTheFile_WhenItIsMissing_AndLeavesNothingElse()
    {
        var path = _storage.File("created.json");

        _atomic.CreateOrRead(path, "first").ShouldBe("first");

        File.ReadAllText(path).ShouldBe("first");
        Directory.GetFileSystemEntries(_storage.Folder).ShouldBe([path]);
    }

    [Test]
    public void ReturnsTheExistingContent_AndKeepsIt()
    {
        var path = _storage.File("existing.json");
        File.WriteAllText(path, "winner");

        _atomic.CreateOrRead(path, "loser").ShouldBe("winner");

        File.ReadAllText(path).ShouldBe("winner");
        Directory.GetFileSystemEntries(_storage.Folder).ShouldBe([path]);
    }

    [Test]
    public async Task OfManyNodesAtOnce_ExactlyOneContentWins_AndEveryoneReturnsIt()
    {
        for (var round = 0; round < 100; round++)
        {
            var path = _storage.File($"race-{round}.json");

            var results = await Task.WhenAll(Enumerable.Range(0, 16)
                .Select(i => Task.Run(() => _atomic.CreateOrRead(path, $"writer-{i}"))));

            results.Distinct().Count().ShouldBe(1, $"round {round}: {string.Join(", ", results.Distinct())}");
            File.ReadAllText(path).ShouldBe(results[0]);
        }

        Directory.GetFileSystemEntries(_storage.Folder).ShouldAllBe(entry => entry.EndsWith(".json", StringComparison.Ordinal));
    }

    [Test]
    public void AClaimLeftByANodeThatStoppedBeforePublishing_IsAdopted()
    {
        var path = _storage.File("crashed.json");
        Directory.CreateDirectory(path + ".claim");
        File.WriteAllText(Path.Combine(path + ".claim", "crashed.json"), "claimed");

        _atomic.CreateOrRead(path, "newcomer").ShouldBe("claimed");

        File.ReadAllText(path).ShouldBe("claimed");
        Directory.Exists(path + ".claim").ShouldBeFalse();
    }

    [Test]
    public void ThePublishedFile_WinsOverALeftoverClaim()
    {
        var path = _storage.File("published.json");
        File.WriteAllText(path, "published");
        Directory.CreateDirectory(path + ".claim");
        File.WriteAllText(Path.Combine(path + ".claim", "published.json"), "stale");

        _atomic.CreateOrRead(path, "newcomer").ShouldBe("published");
    }
}
