using System.Text.Json;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Shouldly;

namespace Registry.Adapters.Test.DroneDB;

/// <summary>
/// The output of <c>DDBCleanupCache</c> read with the contract of libddb's output (<see cref="NativeJson.Options"/>,
/// pure: no libddb): the shape libddb records in its contract fixture <c>cache-cleanup.json</c>, every kind with its
/// entries and bytes, and a kind a later libddb adds is read like the others.
/// </summary>
[TestFixture]
public class CacheCleanupJsonTests
{
    // The "output" of DroneDB's tests/contract/fixtures/cache-cleanup.json: a download of 200 bytes changed 8 days
    // ago and the artefact cache of an earlier version (600 bytes)
    private const string FixtureOutput = """
        {
          "dryRun": false,
          "kinds": {
            "artefacts": { "bytes": 600, "entries": 1 },
            "downloads": { "bytes": 200, "entries": 1 },
            "dsm": { "bytes": 0, "entries": 0 },
            "requests": { "bytes": 0, "entries": 0 },
            "temp": { "bytes": 0, "entries": 0 },
            "tiles": { "bytes": 0, "entries": 0 }
          }
        }
        """;

    private static CacheCleanupResult Read(string json) =>
        JsonSerializer.Deserialize<CacheCleanupResult>(json, NativeJson.Options).ShouldNotBeNull();

    [Test]
    public void TheOutputOfTheContractFixture_IsReadKindByKind()
    {
        var result = Read(FixtureOutput);

        result.DryRun.ShouldBeFalse();
        result.Kinds.Keys.ShouldBe(["artefacts", "downloads", "dsm", "requests", "temp", "tiles"], ignoreOrder: true);
        (result.Kinds["artefacts"].Entries, result.Kinds["artefacts"].Bytes).ShouldBe((1L, 600L));
        (result.Kinds["downloads"].Entries, result.Kinds["downloads"].Bytes).ShouldBe((1L, 200L));
        (result.Kinds["tiles"].Entries, result.Kinds["tiles"].Bytes).ShouldBe((0L, 0L));
        result.TotalEntries.ShouldBe(2);
        result.TotalBytes.ShouldBe(800);
    }

    [Test]
    public void ADryRun_AndAKindOfALaterLibddb_AreRead()
    {
        // Sizes past 4 GiB: a tile cache of a busy server
        var result = Read("""
            {"dryRun": true, "kinds": {"tiles": {"entries": 12000, "bytes": 6442450944}, "thumbnails": {"entries": 3, "bytes": 30}}}
            """);

        result.DryRun.ShouldBeTrue();
        result.Kinds["tiles"].Bytes.ShouldBe(6442450944L);
        result.Kinds["thumbnails"].Entries.ShouldBe(3);
        result.TotalEntries.ShouldBe(12003);
        result.TotalBytes.ShouldBe(6442450974L);
    }

    [Test]
    public void TheTotals_AreNotWrittenBack()
    {
        var written = JsonSerializer.Serialize(Read(FixtureOutput), NativeJson.Options);

        written.ShouldNotContain("total", Case.Insensitive);
    }
}
