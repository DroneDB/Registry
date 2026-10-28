using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Registry.Common;

/// <summary>
/// General utilities: random strings, path helpers, downloads, and platform detection.
/// </summary>
public static class CommonUtils
{
    public static string RandomString(int length)
    {
        const string valid = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
        var res = new StringBuilder();

        using var rng = RandomNumberGenerator.Create();
        var uintBuffer = new byte[sizeof(uint)];

        while (length-- > 0)
        {
            rng.GetBytes(uintBuffer);
            var num = BitConverter.ToUInt32(uintBuffer, 0);
            res.Append(valid[(int)(num % (uint)valid.Length)]);
        }

        return res.ToString();
    }

    public static TValue SafeGetValue<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
    {
        return !dictionary.TryGetValue(key, out var value) ? default : value;
    }

    /// <summary>
    /// Ensures that the sqlite database folder exists
    /// </summary>
    /// <param name="connstr"></param>
    public static void EnsureFolderCreated(string connstr)
    {
        var segments = connstr.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var fields = segment.Split('=');

            if (string.Equals(fields[0], "Data Source", StringComparison.OrdinalIgnoreCase))
            {
                var dbPath = fields[1];

                var folder = Path.GetDirectoryName(dbPath);

                if (folder != null)
                    Directory.CreateDirectory(folder);
            }
        }
    }

    /// <summary>
    /// Combines an array of strings into a path using the forward slash as folder separator
    /// </summary>
    /// <param name="paths">An array of parts of the path.</param>
    /// <returns></returns>
    public static string SafeCombine(params string[] paths)
    {
        return Path.Combine([.. paths.Where(item => item != null)]).ToDdbPath();
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> lies strictly inside <paramref name="root"/>. Both are
    /// normalised with <see cref="Path.GetFullPath(string)"/> (so <c>..</c> segments are resolved and
    /// relative paths are taken from the current directory) and compared per OS (case-insensitive on
    /// Windows). The root ends with a separator before the comparison, so a sibling sharing the prefix
    /// (<c>/data/datasets2</c> for root <c>/data/datasets</c>) is not inside. Symbolic links are not
    /// resolved. Empty or invalid paths are never inside.
    /// </summary>
    public static bool IsUnder(string root, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fullPath))
            return false;

        string rootFull;
        string candidate;

        try
        {
            rootFull = Path.GetFullPath(root);
            candidate = Path.GetFullPath(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // Ensure trailing separator (prevents prefix tricks: C:\base vs C:\baseX)
        if (!Path.EndsInDirectorySeparator(rootFull))
            rootFull += Path.DirectorySeparatorChar;

        return candidate.StartsWith(rootFull, PathComparison.Comparison);
    }

    private static readonly HashSet<string> AddCompressibleMimeTypes =
    [
        "text/html",
        "text/css",
        "text/plain",
        "text/xml",
        "text/csv",
        "text/x-component",
        "text/javascript",
        "application/pdf",
        "application/rtf",
        "application/x-sh",
        "application/x-tar",
        "application/x-javascript",
        "application/javascript",
        "application/json",
        "application/manifest+json",
        "application/vnd.api+json",
        "application/xml",
        "application/xhtml+xml",
        "application/rss+xml",
        "application/atom+xml",
        "application/vnd.ms-fontobject",
        "application/x-font-ttf",
        "application/x-font-opentype",
        "application/x-font-truetype",
        "image/svg+xml",
        "image/x-icon",
        "image/vnd.microsoft.icon",
        "font/ttf",
        "font/eot",
        "font/otf",
        "font/opentype"
    ];

    public static CompressionLevel GetCompressionLevel(string path)
    {
        if (!MimeTypes.TryGetMimeType(path, out var mimeType))
            return CompressionLevel.NoCompression;

        return AddCompressibleMimeTypes.Contains(mimeType)
            ? CompressionLevel.Optimal
            : CompressionLevel.NoCompression;
    }

    // Credit https://stackoverflow.com/a/11124118
    /// <summary>
    /// Returns the human-readable file size for an arbitrary, 64-bit file size
    /// </summary>
    /// <param name="i"></param>
    /// <returns></returns>
    public static string GetBytesReadable(long i)
    {
        // Get absolute value
        var abs = (i < 0 ? -i : i);
        // Determine the suffix and readable value
        string suffix;
        double readable;
        switch (abs)
        {
            // Exabyte
            case >= 0x1000000000000000:
                suffix = "EB";
                readable = (i >> 50);
                break;
            // Petabyte
            case >= 0x4000000000000:
                suffix = "PB";
                readable = (i >> 40);
                break;
            // Terabyte
            case >= 0x10000000000:
                suffix = "TB";
                readable = (i >> 30);
                break;
            // Gigabyte
            case >= 0x40000000:
                suffix = "GB";
                readable = (i >> 20);
                break;
            // Megabyte
            case >= 0x100000:
                suffix = "MB";
                readable = (i >> 10);
                break;
            // Kilobyte
            case >= 0x400:
                suffix = "KB";
                readable = i;
                break;
            default:
                return i.ToString("0 B", CultureInfo.InvariantCulture); // Byte
        }

        // Divide by 1024 to get fractional value
        readable /= 1024;
        // Return formatted number with suffix
        return readable.ToString("0.### ", CultureInfo.InvariantCulture) + suffix;
    }

    public static int BytesPerPixel(string? dataType) => dataType switch
    {
        "Byte" or "UInt8" or "Int8" => 1,
        "UInt16" or "Int16" => 2,
        "UInt32" or "Int32" or "Float32" => 4,
        "Float64" or "UInt64" or "Int64" => 8,
        _ => 4 // Conservative default for unknown types
    };


    public const string DroneDBDllNameWindows = "ddb.dll";
    public const string DroneDBDllNameLinux = "libddb.so";

    public static string FindDdbFolder()
    {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (path == null)
            return null;

        var newPath = path.Split(Path.PathSeparator);

        var libraryName = OperatingSystem.IsWindows()
            ? DroneDBDllNameWindows
            : DroneDBDllNameLinux;

        var ddbFolder = newPath.FirstOrDefault(
            p => File.Exists(Path.Combine(p, libraryName)));

        return ddbFolder;
    }

    /// <summary>
    /// Removes all files and subdirectories inside <paramref name="path"/> without
    /// deleting the directory itself. This is required when the directory is a Linux
    /// bind-mount (e.g. Docker volume): rmdir(2) on a mountpoint returns EBUSY,
    /// which surfaces in .NET as <c>IOException "Device or resource busy"</c>.
    /// </summary>
    public static void EmptyDirectoryContents(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attr = File.GetAttributes(entry);
            if (attr.HasFlag(FileAttributes.ReparsePoint))
            {
                // Symlinks: remove the link, not the target.
                File.Delete(entry);
            }
            else if (attr.HasFlag(FileAttributes.Directory))
            {
                // Subdirectories cannot be mountpoints themselves in our deployment
                // topology, so recursive delete is safe here.
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }
}