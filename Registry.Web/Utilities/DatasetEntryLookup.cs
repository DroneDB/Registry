using System;
using System.IO;
using System.Linq;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities;

/// <summary>An entry of a dataset that holds a name: its dataset path (in its own letter case) and whether it is a folder.</summary>
/// <param name="Path">Dataset-relative path of the entry.</param>
/// <param name="IsFolder">Whether the entry is a folder.</param>
public sealed record ExistingEntry(string Path, bool IsFolder);

/// <summary>Where a name is in a dataset (<see cref="DatasetEntryLookup.Locate"/>).</summary>
/// <param name="Path">The dataset path to write it at, in the letter case of the existing entries.</param>
/// <param name="Existing">The entry that already holds the name, or null when it is free.</param>
public sealed record EntryLocation(string Path, ExistingEntry? Existing);

/// <summary>
/// The one lookup of "does this name already exist" for what the Registry writes into a dataset (D-040,
/// <see cref="InputRules.NamesClashIgnoringCase"/>): names in a folder clash without regard to letter case, so
/// <c>Images/A.tif</c> finds <c>images/a.tif</c>. A tool output, an import, an extraction and a rename ask it,
/// and refuse a clash with <see cref="NameClashException"/> (409 with <c>conflictPath</c>).
/// </summary>
public static class DatasetEntryLookup
{
    /// <summary>
    /// The entry that holds <paramref name="path"/>: an indexed entry whose path matches it without regard to
    /// letter case (each segment), or a file or folder on disk at that path that the index does not list.
    /// </summary>
    /// <param name="ddb">The dataset.</param>
    /// <param name="path">A normalized dataset path (see <see cref="DatasetPathPolicy.Normalize"/>).</param>
    /// <returns>The existing entry, or null when the name is free.</returns>
    public static ExistingEntry? FindExisting(IDDB ddb, string path) => Locate(ddb, path).Existing;

    /// <summary>
    /// Where <paramref name="path"/> is in the dataset (D-040): the existing entry that holds it, if any, and the path
    /// to write it at. That path keeps the letter case of the existing entries: of the entry itself, or, for a free
    /// name, of the folders it is in (<c>Out/new.tif</c> in the indexed folder <c>out</c> is <c>out/new.tif</c>, so
    /// no sibling folder <c>Out</c> appears on a case-sensitive file system).
    /// </summary>
    /// <param name="ddb">The dataset.</param>
    /// <param name="path">A normalized dataset path.</param>
    /// <returns>The path to write and the existing entry (null when the name is free).</returns>
    public static EntryLocation Locate(IDDB ddb, string path)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (ddb.EntryExists(path))
            return new EntryLocation(path, new ExistingEntry(path, ddb.GetEntry(path)?.Type == EntryType.Directory));

        var (indexed, located) = WalkIgnoringCase(ddb, path);
        if (indexed is not null)
            return new EntryLocation(indexed.Path, indexed);

        // A file the index does not list (an interrupted write, a file copied in by hand) is replaced just the same
        var local = ddb.GetLocalPath(located);
        if (!string.IsNullOrEmpty(local))
        {
            if (File.Exists(local))
                return new EntryLocation(located, new ExistingEntry(located, false));
            if (Directory.Exists(local))
                return new EntryLocation(located, new ExistingEntry(located, true));
        }

        return new EntryLocation(located, null);
    }

    /// <summary>
    /// Where an output named <paramref name="path"/> is written: the path of <see cref="Locate"/> when the name is
    /// free, the path of the existing entry when <paramref name="overwrite"/> allows replacing it (a name that differs
    /// only in letter case replaces that entry, D-040).
    /// </summary>
    /// <param name="ddb">The dataset.</param>
    /// <param name="path">A normalized dataset path.</param>
    /// <param name="overwrite">Whether the request allows replacing an existing file.</param>
    /// <returns>The dataset path to write.</returns>
    /// <exception cref="NameClashException">The name exists and <paramref name="overwrite"/> is false, or the name is a folder.</exception>
    public static string ResolveOutput(IDDB ddb, string path, bool overwrite)
    {
        var location = Locate(ddb, path);
        if (location.Existing is not { } existing)
            return location.Path;

        // A folder is never replaced by a file
        if (!overwrite || existing.IsFolder)
            throw new NameClashException(existing.Path);

        return existing.Path;
    }

    // Walks the path one segment at a time through the index, matching each name without regard to case (an exact
    // match first, so two entries that differ only in case resolve to the one asked for). Returns the entry when
    // every segment matched, and the path in the case of the segments that did, followed by the others
    private static (ExistingEntry? Entry, string Path) WalkIgnoringCase(IDDB ddb, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        EntrySummary? match = null;

        for (var i = 0; i < segments.Length; i++)
        {
            var parent = current;
            var children = (ddb.FindSummaries(new EntryQuery(parent, Recursive: false)) ?? [])
                .Where(c => c.Path is { Length: > 0 } && string.Equals(ParentOf(c.Path), parent, StringComparison.Ordinal))
                .ToList();

            match = children.FirstOrDefault(c => string.Equals(NameOf(c.Path), segments[i], StringComparison.Ordinal))
                    ?? children.FirstOrDefault(c =>
                        string.Equals(NameOf(c.Path), segments[i], StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                var rest = string.Join('/', segments[i..]);
                return (null, parent.Length == 0 ? rest : parent + "/" + rest);
            }

            current = match.Path;
        }

        return match is null ? (null, path) : (new ExistingEntry(match.Path, match.Type == EntryType.Directory), match.Path);
    }

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string NameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
