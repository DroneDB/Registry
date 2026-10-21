#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Registry.Web.Test.Support;

/// <summary>
/// A checked-in, line-based snapshot under <c>ImprovementBaselines/Snapshots</c> of the running test
/// project (e.g. <c>Registry.Web.UnitTest</c>).
/// It is read from the source tree when the tests run inside the repository (so an update is seen
/// without a rebuild), else from the copy in the test output. <see cref="Verify"/> fails with a diff on any
/// difference; with <see cref="UpdateVariable"/>=1 it rewrites the checked-in file instead, so a
/// change is always made on purpose and reviewed in the diff.
/// Lines starting with <c>#</c> are comments: they are written as given and ignored by the
/// comparison.
/// </summary>
public static class SnapshotFile
{
    public const string UpdateVariable = "REGISTRY_UPDATE_SNAPSHOTS";

    private const string SnapshotsFolder = "Snapshots";
    private const string BaselinesFolder = "ImprovementBaselines";
    private const string ProjectFilePattern = "*.csproj";
    private const string CommentPrefix = "#";

    /// <summary>
    /// Compares <paramref name="actual"/> with the snapshot <paramref name="name"/>. Returns null
    /// when they match (or the snapshot was updated), otherwise a message with the diff.
    /// </summary>
    public static string? Verify(string name, IReadOnlyList<string> header, IReadOnlyList<string> actual)
    {
        var sourceFolder = SourceSnapshotsFolder();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            if (sourceFolder == null)
                throw new DirectoryNotFoundException($"Cannot find the test project folder above {AppContext.BaseDirectory}");

            Directory.CreateDirectory(sourceFolder);
            File.WriteAllText(Path.Combine(sourceFolder, name), string.Join('\n', header.Concat(actual)) + "\n",
                new UTF8Encoding(false));
            return null;
        }

        var expectedPath = Path.Combine(
            sourceFolder ?? Path.Combine(AppContext.BaseDirectory, BaselinesFolder, SnapshotsFolder), name);
        var expected = File.Exists(expectedPath)
            ? File.ReadAllLines(expectedPath).Where(l => l.Length > 0 && !l.StartsWith(CommentPrefix, StringComparison.Ordinal)).ToList()
            : [];

        var diff = Diff(expected, actual);
        if (diff.Count == 0)
            return null;

        return $"Snapshot {BaselinesFolder}/{SnapshotsFolder}/{name} differs ({diff.Count} line(s)). " +
               $"If the change is intended, run the test with {UpdateVariable}=1 and commit the file.\n" +
               string.Join('\n', diff);
    }

    /// <summary>"- line" for lines only in <paramref name="expected"/>, "+ line" for new ones.</summary>
    public static IReadOnlyList<string> Diff(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var removed = expected.Except(actual, StringComparer.Ordinal).Select(l => (Line: l, Text: "- " + l));
        var added = actual.Except(expected, StringComparer.Ordinal).Select(l => (Line: l, Text: "+ " + l));

        return removed.Concat(added)
            .OrderBy(x => x.Line, StringComparer.Ordinal)
            .ThenBy(x => x.Text, StringComparer.Ordinal)
            .Select(x => x.Text)
            .ToList();
    }

    /// <summary>
    /// The snapshots folder of the source tree above the test output, or null outside it: the test
    /// project folder is the first folder above the output (bin/&lt;config&gt;/&lt;tfm&gt;) with a csproj.
    /// </summary>
    private static string? SourceSnapshotsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles(ProjectFilePattern).Any())
                return Path.Combine(dir.FullName, BaselinesFolder, SnapshotsFolder);
        }

        return null;
    }
}
