#nullable enable
using System;
using System.IO;
using System.Text;

namespace Registry.Web.Utilities;

/// <summary>
/// Writes a file so that a reader sees the old content or the new one, never a part (the storage
/// folder's <c>appsettings.json</c>, read by every Registry container sharing the folder).
/// </summary>
public static class AtomicFile
{
    /// <summary>errno of a rename onto a mount point (Linux and macOS): a bind-mounted single file.</summary>
    private const int Ebusy = 16;

    /// <summary>
    /// Writes <paramref name="content"/> (UTF-8, no BOM) to a temporary file next to <paramref name="path"/>,
    /// flushes it to disk and renames it over <paramref name="path"/>. A file that is itself a mount point
    /// (Docker's <c>-v ./appsettings.json:/data/appsettings.json</c>) cannot be replaced by a rename
    /// (<c>EBUSY</c>): only then the prepared content is copied into it in place.
    /// </summary>
    /// <returns>True when the file was replaced atomically, false when it was written in place.</returns>
    public static bool WriteAllText(string path, string content) => WriteAllText(path, content, Replace);

    /// <summary><see cref="WriteAllText(string,string)"/> with the rename step injected (tests).</summary>
    internal static bool WriteAllText(string path, string content, Action<string, string> replace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(replace);

        var fullPath = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(fullPath) ?? ".";
        var temp = Path.Combine(folder, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var bytes = new UTF8Encoding(false).GetBytes(content);

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                replace(temp, fullPath);
                return true;
            }
            catch (IOException ex) when (ex.HResult == Ebusy && !OperatingSystem.IsWindows())
            {
                WriteInPlace(fullPath, bytes);
                return false;
            }
        }
        finally
        {
            // No-op when the rename consumed it
            File.Delete(temp);
        }
    }

    private static void Replace(string source, string destination) => File.Move(source, destination, overwrite: true);

    private static void WriteInPlace(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
