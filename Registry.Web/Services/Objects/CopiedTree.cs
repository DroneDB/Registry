using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Registry.Common;
using Registry.Ports;

namespace Registry.Web.Services.Objects;

/// <summary>The size and write time (UTC) a copied file had.</summary>
internal readonly record struct CopiedFile(long Size, DateTime ModifiedUtc)
{
    /// <summary>The size and write time of the file at <paramref name="localPath"/>.</summary>
    public static CopiedFile Of(IFileSystem fs, string localPath) =>
        new(fs.GetFileSize(localPath), fs.GetLastWriteTime(localPath).ToUniversalTime());
}

/// <summary>
/// What a copy of an entry holds: its files, by path relative to the entry ("" for the entry itself when it is a
/// file), with the size and write time of each, and its subfolders. Read from a copy nobody else writes (the
/// temporary sibling of a placement before it is promoted), so it is exactly what the destination received; a file
/// copy keeps the write time of its source, so a source file still holds what was copied while it has both.
/// </summary>
internal sealed class CopiedTree
{
    private CopiedTree(IReadOnlyDictionary<string, CopiedFile> files, IReadOnlyList<string> folders)
    {
        Files = files;
        Folders = folders;
    }

    /// <summary>The copied files, by relative path with '/' separators.</summary>
    public IReadOnlyDictionary<string, CopiedFile> Files { get; }

    /// <summary>The copied subfolders (relative, '/' separators), the deepest first.</summary>
    public IReadOnlyList<string> Folders { get; }

    /// <summary>The tree of the copy at <paramref name="localPath"/>, a folder or a file.</summary>
    public static CopiedTree Read(IFileSystem fs, string localPath, bool isFolder)
    {
        if (!isFolder)
            return new CopiedTree(new Dictionary<string, CopiedFile>(StringComparer.Ordinal)
            {
                [string.Empty] = CopiedFile.Of(fs, localPath)
            }, []);

        var files = fs.EnumerateFiles(localPath, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Relative(localPath, file), file => CopiedFile.Of(fs, file), StringComparer.Ordinal);
        var folders = fs.EnumerateDirectories(localPath, "*", SearchOption.AllDirectories)
            .Select(folder => Relative(localPath, folder))
            .OrderByDescending(folder => folder.Count(c => c == '/'))
            .ToArray();
        return new CopiedTree(files, folders);
    }

    /// <summary>The path of <paramref name="path"/> relative to <paramref name="root"/>, with '/' separators.</summary>
    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).ToDdbPath();
}
