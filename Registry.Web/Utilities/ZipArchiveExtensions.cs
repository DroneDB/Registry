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
        {
            foreach (var child in Directory.GetFiles(sourceName).Concat(Directory.GetDirectories(sourceName)))
                await archive.CreateEntryFromAnyAsync(child, path, excludes, ct);
        }
        else
        {
            await archive.CreateEntryFromFileAsync(sourceName, path, CommonUtils.GetCompressionLevel(path), ct);
        }
    }
}
