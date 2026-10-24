#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Web.Utilities;

/// <summary>
/// Indexes a byte copy of indexed entries (transfer, copy) without hashing it again (XI-05): each
/// copied file is added with the source entry's hash as a hint (<c>DDBAddWithHints</c>), with the
/// copy's own size and write time, so libddb trusts the hash only while the copy is unchanged. A
/// source file is hinted only while it still has its entry's size and write time (libddb's own
/// trust rule, applied to the source: a file changed on disk after it was indexed is hashed).
/// </summary>
public static class CopiedEntries
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Indexes <paramref name="destPath"/> of <paramref name="dest"/>, a byte copy of
    /// <paramref name="sourceEntry"/> of <paramref name="source"/> (a file, or a folder and everything
    /// in it). A folder is then added as a whole, as before: its subfolders and any file the source
    /// index did not hold are added (and hashed) by libddb, the hinted files are unchanged and skipped.
    /// </summary>
    /// <exception cref="DdbException">libddb could not index a copied file.</exception>
    public static void Index(IDDB source, EntrySummary sourceEntry, IDDB dest, string destPath, IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(sourceEntry);
        var directory = sourceEntry.Type == EntryType.Directory;
        var hints = Hints(source, sourceEntry, dest, destPath, fs);
        if (hints.Count > 0)
        {
            var result = dest.AddRawBatchWithOptions(hints, stopOnError: true);
            if (result.Errors.Count > 0)
                throw new DdbException($"Cannot index '{result.Errors[0].Path}': {result.Errors[0].Message}");
        }

        if (directory || hints.Count == 0)
            dest.AddRaw(destPath);
    }

    /// <summary>The hinted adds of the copied files (destination paths).</summary>
    internal static List<AddItem> Hints(IDDB source, EntrySummary sourceEntry, IDDB dest, string destPath,
        IFileSystem fs)
    {
        var sourcePath = sourceEntry.Path;
        var directory = sourceEntry.Type == EntryType.Directory;
        IEnumerable<EntrySummary> summaries = directory
            ? source.FindSummaries(EntryQuery.Under(sourcePath))
                .Where(s => s.Path.StartsWith(sourcePath + "/", StringComparison.Ordinal))
            : [sourceEntry];

        var items = new List<AddItem>();
        foreach (var entry in summaries)
        {
            if (entry.Type == EntryType.Directory || string.IsNullOrEmpty(entry.Hash))
                continue;

            var sourceFile = source.GetLocalPath(entry.Path);
            if (!fs.Exists(sourceFile) || fs.GetFileSize(sourceFile) != entry.Size ||
                !SameModifiedTime(entry.ModifiedTime, fs.GetLastWriteTime(sourceFile).ToUniversalTime()))
                continue;

            var target = directory ? destPath + entry.Path[sourcePath.Length..] : destPath;
            var copy = dest.GetLocalPath(target);
            if (!fs.Exists(copy))
                continue;

            items.Add(new AddItem(target, new FileHashHint(entry.Hash, fs.GetFileSize(copy),
                fs.GetLastWriteTime(copy).ToUniversalTime())));
        }

        return items;
    }

    // The index keeps sub-second times since DQ-09 and whole seconds before: a whole-second time
    // matches the file's time truncated to the second, as libddb compares them
    private static bool SameModifiedTime(DateTime indexedUtc, DateTime fileUtc)
    {
        var indexed = (indexedUtc - Epoch).TotalSeconds;
        var file = (fileUtc - Epoch).TotalSeconds;
        return Math.Abs(indexed - file) < 0.001 ||
               (indexed == Math.Floor(indexed) && Math.Floor(file) == indexed);
    }
}
