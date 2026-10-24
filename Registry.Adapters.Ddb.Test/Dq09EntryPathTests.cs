#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.LibddbNative;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// DQ-09 on the real libddb (2c9508a): every export that takes a dataset-relative entry path applies the rule of
/// <c>DDBNormalizePath</c> to it, and refuses a path that is absolute, has a <c>..</c> segment or names <c>.ddb</c> with
/// <c>DDBERR_INVALIDARGS</c>, which the adapter maps to <see cref="DdbInvalidArgumentsException"/> (a 400 for the client,
/// see <c>ApiExceptionClassifier</c>) for every one of those exports, also the ones of libddb 1.x that used to answer
/// <c>DDBERR_EXCEPTION</c> for an argument they did not like. Registry's own paths are dataset-relative already
/// (<c>DatasetPathPolicy</c>, <c>DDB.GetEntry</c> making the entry relative): this is the net under them.
/// Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~Dq09EntryPathTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
public class Dq09EntryPathTests : TestBase
{
    private static readonly NativeDdbWrapper Wrapper = new();

    [OneTimeSetUp]
    public void Setup()
    {
        UseDdbFolder();
        var (gate, message) = CheckAbi();
        if (gate != Gate.Applies)
            Assert.Fail(message);
    }

    // The three ways the rule refuses a path (the absolute one is made inside the test: it names a real folder)
    private static readonly string[] RefusedRelative = ["../outside.txt", "dir/../../outside.txt", ".ddb/index", "a/.ddb/b"];

    private static string[] Refused(string root) => [.. RefusedRelative, Path.Combine(root, "dir", "a.txt")];

    /// <summary>A dataset with <c>dir/a.txt</c> and <c>b.txt</c>, indexed.</summary>
    private static string CreateTextDataset(TestArea area)
    {
        var root = Path.Combine(area.TestFolder, "dataset");
        Directory.CreateDirectory(Path.Combine(root, "dir"));
        Wrapper.Init(root);
        File.WriteAllText(Path.Combine(root, "dir", "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(root, "b.txt"), "bravo");
        Wrapper.Add(root, [Path.Combine(root, "dir", "a.txt"), Path.Combine(root, "b.txt")]);
        return root;
    }

    /// <summary>Each export that takes an entry path, called with <paramref name="path"/> in its path argument.</summary>
    private static IEnumerable<(string Export, Action Call)> EntryPathCalls(string root, string path)
    {
        yield return (nameof(NativeDdb.DDBGet), () => Wrapper.GetEntry(root, path));
        yield return (nameof(NativeDdb.DDBListEntriesEx), () => Wrapper.ListEntries(root, new EntryQuery(path)));
        yield return ("DDBListEntriesEx(summaries)", () => Wrapper.ListEntrySummaries(root, new EntryQuery(path)));
        yield return (nameof(NativeDdb.DDBMoveEntry) + " (source)", () => Wrapper.MoveEntry(root, path, "moved.txt"));
        yield return (nameof(NativeDdb.DDBMoveEntry) + " (dest)", () => Wrapper.MoveEntry(root, "b.txt", path));
        yield return (nameof(NativeDdb.DDBBuild), () => Wrapper.Build(root, path));
        yield return (nameof(NativeDdb.DDBGetBuildArtifacts), () => Wrapper.GetBuildArtifacts(root, [path]));
        yield return (nameof(NativeDdb.DDBMetaAdd), () => Wrapper.MetaAdd(root, "annotations", "123", path));
        yield return (nameof(NativeDdb.DDBMetaSet), () => Wrapper.MetaSet(root, "test", "abc", path));
        yield return (nameof(NativeDdb.DDBMetaGet), () => Wrapper.MetaGet(root, "test", path));
        yield return (nameof(NativeDdb.DDBMetaUnset), () => Wrapper.MetaUnset(root, "test", path));
        yield return (nameof(NativeDdb.DDBMetaList), () => Wrapper.MetaList(root, path));
        yield return (nameof(NativeDdb.DDBStacData), () => Wrapper.StacData(root, path));
    }

    [Test]
    public void ARefusedEntryPath_IsInvalidArguments_OnEveryExportThatTakesOne_AndChangesNothing()
    {
        using var area = new TestArea(nameof(ARefusedEntryPath_IsInvalidArguments_OnEveryExportThatTakesOne_AndChangesNothing));
        var root = CreateTextDataset(area);
        var before = Wrapper.ListEntries(root, EntryQuery.All).Select(e => e.Path).Order().ToArray();

        foreach (var path in Refused(root))
        foreach (var (export, call) in EntryPathCalls(root, path))
        {
            var ex = Should.Throw<DdbInvalidArgumentsException>(call, $"{export} with '{path}'");
            ex.Message.ShouldContain("Invalid", Case.Sensitive, $"{export} with '{path}'");
        }

        Wrapper.ListEntries(root, EntryQuery.All).Select(e => e.Path).Order().ShouldBe(before, ignoreOrder: false, "a refused path changes nothing");
        File.Exists(Path.Combine(root, "b.txt")).ShouldBeTrue();
        Wrapper.MetaList(root).ShouldBeEmpty();
    }

    [Test]
    public void ARefusedDeltaPath_IsInvalidArguments_AndAppliesNothing()
    {
        using var area = new TestArea(nameof(ARefusedDeltaPath_IsInvalidArguments_AndAppliesNothing));
        var root = CreateTextDataset(area);
        var addFolder = Path.Combine(root, ".ddb", "tmp", "push-test", "add");
        Directory.CreateDirectory(addFolder);
        File.WriteAllText(Path.Combine(addFolder, "good.txt"), "good");
        var goodHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("good"u8.ToArray()));

        foreach (var path in Refused(root))
        {
            var delta = new Delta
            {
                Adds = [new AddAction("good.txt", goodHash), new AddAction(path, "")], Removes = [], MetaAdds = [],
                MetaRemoves = []
            };

            Should.Throw<DdbInvalidArgumentsException>(
                () => Wrapper.ApplyDelta(delta, addFolder, root, MergeStrategy.KeepTheirs, null, new Dictionary<string, string>()),
                $"ApplyDelta with '{path}'");
            Should.Throw<DdbInvalidArgumentsException>(() => Wrapper.ComputeDeltaLocals(delta, root),
                $"ComputeDeltaLocals with '{path}'");
        }

        File.Exists(Path.Combine(addFolder, "good.txt")).ShouldBeTrue("the file that was fine stays: the call is refused as a whole");
        Wrapper.GetEntry(root, "good.txt").ShouldBeNull();
    }

    /// <summary>A valid path in another spelling is the same entry (what <c>DatasetPathPolicy</c> sends is already normal).</summary>
    [Test]
    public void AnotherSpellingOfAValidPath_IsTheSameEntry()
    {
        using var area = new TestArea(nameof(AnotherSpellingOfAValidPath_IsTheSameEntry));
        var root = CreateTextDataset(area);

        foreach (var spelling in new[] { "dir/a.txt", "dir\\a.txt", "./dir//a.txt", "dir/a.txt/" })
            Wrapper.GetEntry(root, spelling)!.Path.ShouldBe("dir/a.txt", spelling);
    }
}
