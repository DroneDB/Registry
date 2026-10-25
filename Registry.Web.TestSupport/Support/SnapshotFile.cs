#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Registry.Web.Test.Support;

/// <summary>
/// A checked-in, line-based snapshot under <c>Baselines/Snapshots</c> of the running test
/// project (e.g. <c>Registry.Web.UnitTest</c>).
/// It is read from the source tree when the tests run inside the repository (so an update is seen
/// without a rebuild), else from the copy in the test output. <see cref="Verify"/> fails with a diff on any
/// difference; with <see cref="UpdateVariable"/>=1 it rewrites the checked-in file instead, so a
/// change is always made on purpose and reviewed in the diff.
/// Lines starting with <c>#</c> are comments: they are written as given and ignored by the
/// comparison.
/// <see cref="VerifyDocument"/> is the same contract for a whole document (e.g. a golden JSON body,
/// <c>name</c> may contain a sub-folder): compared as exact text, with an ordered line diff.
/// </summary>
public static class SnapshotFile
{
    public const string UpdateVariable = "REGISTRY_UPDATE_SNAPSHOTS";

    private const string SnapshotsFolder = "Snapshots";
    private const string BaselinesFolder = "Baselines";
    private const string ProjectFilePattern = "*.csproj";
    private const string CommentPrefix = "#";

    /// <summary>
    /// Compares <paramref name="actual"/> with the snapshot <paramref name="name"/>. Returns null
    /// when they match (or the snapshot was updated), otherwise a message with the diff.
    /// </summary>
    public static string? Verify(string name, IReadOnlyList<string> header, IReadOnlyList<string> actual)
    {
        if (TryUpdate(name, string.Join('\n', header.Concat(actual)) + "\n"))
            return null;

        var expectedPath = PathOf(name);
        var expected = File.Exists(expectedPath)
            ? File.ReadAllLines(expectedPath).Where(l => l.Length > 0 && !l.StartsWith(CommentPrefix, StringComparison.Ordinal)).ToList()
            : [];

        var diff = Diff(expected, actual);
        return diff.Count == 0 ? null : Failure(name, diff.Count, diff);
    }

    /// <summary>
    /// Compares the whole text <paramref name="actual"/> with the snapshot document
    /// <paramref name="name"/> (no comment lines, line order matters; line endings are normalised to
    /// LF). Returns null when they match (or the snapshot was updated), otherwise a message with an
    /// ordered diff (<see cref="OrderedDiff"/>).
    /// </summary>
    public static string? VerifyDocument(string name, string actual)
    {
        var text = NormaliseLineEndings(actual).TrimEnd('\n') + "\n";
        if (TryUpdate(name, text))
            return null;

        var expectedPath = PathOf(name);
        var expected = File.Exists(expectedPath) ? NormaliseLineEndings(File.ReadAllText(expectedPath)) : "";
        if (string.Equals(expected, text, StringComparison.Ordinal))
            return null;

        var diff = OrderedDiff(SplitLines(expected), SplitLines(text));
        var changed = diff.Count(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("+ ", StringComparison.Ordinal));
        return Failure(name, changed, File.Exists(expectedPath) ? diff : ["(no snapshot file yet)", .. diff]);
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
    /// An ordered line diff of two documents (longest common subsequence): changed lines as
    /// <c>"- "</c> (only in <paramref name="expected"/>) and <c>"+ "</c> (only in
    /// <paramref name="actual"/>), each hunk with up to <paramref name="context"/> unchanged lines
    /// (<c>"  "</c>) around it and introduced by <c>"@@ line N"</c> (1-based, in
    /// <paramref name="actual"/>).
    /// </summary>
    public static IReadOnlyList<string> OrderedDiff(IReadOnlyList<string> expected, IReadOnlyList<string> actual,
        int context = 2)
    {
        // lcs[i, j] = length of the LCS of expected[i..] and actual[j..]
        var lcs = new int[expected.Count + 1, actual.Count + 1];
        for (var i = expected.Count - 1; i >= 0; i--)
        for (var j = actual.Count - 1; j >= 0; j--)
            lcs[i, j] = string.Equals(expected[i], actual[j], StringComparison.Ordinal)
                ? lcs[i + 1, j + 1] + 1
                : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var script = new List<(char Op, string Line, int ActualLine)>();
        int e = 0, a = 0;
        while (e < expected.Count || a < actual.Count)
        {
            if (e < expected.Count && a < actual.Count && string.Equals(expected[e], actual[a], StringComparison.Ordinal))
                script.Add((' ', expected[e++], ++a));
            else if (e < expected.Count && (a == actual.Count || lcs[e + 1, a] >= lcs[e, a + 1]))
                script.Add(('-', expected[e++], a + 1));
            else
                script.Add(('+', actual[a++], a));
        }

        var output = new List<string>();
        var lastPrinted = -1;
        for (var k = 0; k < script.Count; k++)
        {
            if (script[k].Op == ' ')
                continue;

            var from = Math.Max(k - context, lastPrinted + 1);
            if (from > lastPrinted + 1 || lastPrinted < 0)
                output.Add($"@@ line {script[from].ActualLine}");

            var to = k;
            for (var n = k; n < script.Count && n <= to + context; n++)
                if (script[n].Op != ' ')
                    to = n;
            to = Math.Min(to + context, script.Count - 1);

            for (var n = from; n <= to; n++)
                output.Add($"{script[n].Op} {script[n].Line}");

            lastPrinted = to;
            k = to;
        }

        return output;
    }

    private static string Failure(string name, int changedLines, IEnumerable<string> diff) =>
        $"Snapshot {BaselinesFolder}/{SnapshotsFolder}/{name} differs ({changedLines} line(s)). " +
        $"If the change is intended, run the test with {UpdateVariable}=1 and commit the file.\n" +
        string.Join('\n', diff);

    /// <summary>With <see cref="UpdateVariable"/>=1, writes <paramref name="text"/> to the source snapshot and returns true.</summary>
    private static bool TryUpdate(string name, string text)
    {
        if (Environment.GetEnvironmentVariable(UpdateVariable) != "1")
            return false;

        var sourceFolder = SourceSnapshotsFolder()
                           ?? throw new DirectoryNotFoundException(
                               $"Cannot find the test project folder above {AppContext.BaseDirectory}");
        var path = Path.Combine(sourceFolder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return true;
    }

    /// <summary>
    /// The file the snapshot <paramref name="name"/> is read from: in the source tree when the tests
    /// run inside the repository, else in the test output (a sub-folder name, e.g. <c>golden</c>,
    /// gives that folder).
    /// </summary>
    public static string PathOf(string name) =>
        Path.Combine(SourceSnapshotsFolder() ?? Path.Combine(AppContext.BaseDirectory, BaselinesFolder, SnapshotsFolder),
            name);

    private static string NormaliseLineEndings(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string[] SplitLines(string text) => text.Length == 0 ? [] : text.TrimEnd('\n').Split('\n');

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
