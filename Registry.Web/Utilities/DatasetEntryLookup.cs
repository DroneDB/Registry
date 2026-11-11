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
/// <c>Images/A.tif</c> finds <c>images/a.tif</c>. Each folder level is one index query for that name (libddb's
/// name pattern ignores the case of ASCII letters), then the disk for a file the index does not list: never a listing
/// of the folder. A tool output, an import, an extraction and a rename ask it,
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

        // An indexed entry, or one on disk that the index does not list (an interrupted write, a file copied in by
        // hand), which is replaced just the same
        var (found, located, indexedType) = WalkIgnoringCase(ddb, path);
        if (!found)
            return new EntryLocation(located, null);
        var isFolder = indexedType is { } type
            ? type == EntryType.Directory
            : Directory.Exists(ddb.GetLocalPath(located));
        return new EntryLocation(located, new ExistingEntry(located, isFolder));
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

    // What a folder enumeration of NameOnDisk sees: hidden entries too, any letter case
    private static readonly EnumerationOptions OneNameIgnoringCase = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        MatchType = MatchType.Simple,
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    // Walks the path one segment at a time: the entry of the index with that name (an exact match first, so two entries
    // that differ only in case resolve to the one asked for), else the name on disk, both without regard to case. Each
    // segment costs one index query filtered by the name and, when the index has no such entry, one folder
    // enumeration filtered by it: a burst of uploads into a large folder never lists it once per file. Returns whether
    // every segment matched, the path in the case of the segments that did followed by the others, and the indexed
    // type of the last one (null when only the disk has it)
    private static (bool Found, string Path, EntryType? IndexedType) WalkIgnoringCase(IDDB ddb, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        EntryType? indexedType = null;

        for (var i = 0; i < segments.Length; i++)
        {
            var indexed = IndexedChild(ddb, current, segments[i]);
            var name = indexed is null ? NameOnDisk(ddb.GetLocalPath(current), segments[i]) : NameOf(indexed.Path);
            if (name is null)
                return (false, Join(current, string.Join('/', segments[i..])), null);

            indexedType = indexed?.Type;
            current = Join(current, name);
        }

        return (segments.Length > 0, segments.Length > 0 ? current : path, indexedType);
    }

    // The indexed child of `folder` called `name` without regard to case (the exact name first), or null. libddb's
    // pattern matches '*' and '?' as wildcards and ASCII letters in any case: the names are compared here again
    private static EntrySummary? IndexedChild(IDDB ddb, string folder, string name)
    {
        var children = (ddb.FindSummaries(new EntryQuery(folder, Recursive: false, NamePattern: name)) ?? [])
            .Where(c => c.Path is { Length: > 0 } && string.Equals(ParentOf(c.Path), folder, StringComparison.Ordinal))
            .ToList();

        return children.FirstOrDefault(c => string.Equals(NameOf(c.Path), name, StringComparison.Ordinal))
               ?? children.FirstOrDefault(c => string.Equals(NameOf(c.Path), name, StringComparison.OrdinalIgnoreCase));
    }

    // The name of the entry of `folder` called `name` without regard to letter case (the exact name first), or null
    private static string? NameOnDisk(string? folder, string name)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return null;

        // A wildcard in the name would match other names too: the filter below then does all the matching
        var pattern = name.IndexOfAny(['*', '?']) >= 0 ? "*" : name;
        string? match = null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, pattern, OneNameIgnoringCase))
        {
            var candidate = Path.GetFileName(entry);
            if (string.Equals(candidate, name, StringComparison.Ordinal))
                return candidate;
            if (match is null && string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                match = candidate;
        }

        return match;
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

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
