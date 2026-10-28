#nullable enable
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Common;

namespace Registry.Web.Utilities;

public static class ZipArchiveExtension
{
    /// <summary>
    /// Adds a file, or a folder with everything under it, to the archive under
    /// <paramref name="entryName"/>. Paths listed in <paramref name="excludes"/> (full paths) are
    /// skipped with their content. Asynchronous throughout, so the archive can be written straight
    /// to a response body that refuses synchronous writes.
    /// </summary>
    public static async Task CreateEntryFromAnyAsync(this ZipArchive archive, string sourceName,
        string entryName = "", string[]? excludes = null, CancellationToken ct = default)
    {
        if (excludes != null && excludes.Contains(sourceName)) return;

        ct.ThrowIfCancellationRequested();
        var path = CommonUtils.SafeCombine(entryName, Path.GetFileName(sourceName));

        if (File.GetAttributes(sourceName).HasFlag(FileAttributes.Directory))
            await archive.CreateEntriesFromFolderAsync(sourceName, path, excludes, ct);
        else
            await archive.CreateEntryFromFileAsync(sourceName, path, CommonUtils.GetCompressionLevel(path), ct);
    }

    /// <summary>
    /// Adds what is in a folder (its files, and its subfolders with everything under them) to the archive under
    /// <paramref name="entryName"/>, without an entry for the folder itself: the archive of a folder has the folder's
    /// content at its root when <paramref name="entryName"/> is empty.
    /// </summary>
    public static async Task CreateEntriesFromFolderAsync(this ZipArchive archive, string folder,
        string entryName = "", string[]? excludes = null, CancellationToken ct = default)
    {
        foreach (var child in Directory.GetFiles(folder).Concat(Directory.GetDirectories(folder)))
            await archive.CreateEntryFromAnyAsync(child, entryName, excludes, ct);
    }
}
