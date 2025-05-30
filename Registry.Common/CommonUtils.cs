﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
using Registry.Common.Model;
using ZipFile = System.IO.Compression.ZipFile;

namespace Registry.Common;

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

    private const int BytesToRead = sizeof(long);

    public static bool FilesAreEqual(string first, string second)
    {
        return FilesAreEqual(new FileInfo(first), new FileInfo(second));
    }

    public static bool FilesAreEqual(FileInfo first, FileInfo second)
    {
        if (first.Length != second.Length)
            return false;

        if (string.Equals(first.FullName, second.FullName, StringComparison.OrdinalIgnoreCase))
            return true;

        var iterations = (int)Math.Ceiling((double)first.Length / BytesToRead);

        using var fs1 = first.OpenRead();
        using var fs2 = second.OpenRead();

        var one = new byte[BytesToRead];
        var two = new byte[BytesToRead];

        for (var i = 0; i < iterations; i++)
        {
            var c1 = fs1.Read(one, 0, BytesToRead);
            var c2 = fs2.Read(two, 0, BytesToRead);

            if (c1 != c2)
                return false;

            if (BitConverter.ToInt64(one, 0) != BitConverter.ToInt64(two, 0))
                return false;
        }

        return true;
    }

    public static TValue SafeGetValue<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
    {
        return !dictionary.TryGetValue(key, out var value) ? default : value;
    }

    public static TValue GetOrAdd<TKey, TValue>(
        this Dictionary<TKey, TValue> dict, TKey key, TValue value) where TKey : notnull
    {
        ref var val = ref CollectionsMarshal.GetValueRefOrAddDefault(dict, key, out var exists);

        if (exists)
            return val;

        val = value;
        return value;
    }

    public static TValueOut? SafeGetValue<TKey, TValue, TValueOut>(this IDictionary<TKey, TValue> dictionary,
        TKey key, Func<TValue, TValueOut> selector) where TValueOut : struct
    {
        return !dictionary.TryGetValue(key, out var value) ? null : selector(value);
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
    /// Creates the parent folder if it does not exist
    /// </summary>
    /// <param name="path"></param>
    public static void EnsureSafePath(string path)
    {
        var folder = Path.GetDirectoryName(path);

        if (folder != null)
            Directory.CreateDirectory(folder);
    }

    public static void SmartExtractFolder(string archive, string dest, bool overwrite = true)
    {
        var ext = Path.GetExtension(archive).ToLowerInvariant();

        if (ext == ".tar.gz" || ext == ".tgz")
            ExtractTGZ(archive, dest);
        else
            ZipFile.ExtractToDirectory(archive, dest, overwrite);
    }

    public static void ExtractTGZ(string archive, string destFolder)
    {
        using Stream inStream = File.OpenRead(archive);
        using Stream gzipStream = new GZipInputStream(inStream);

        var tarArchive = TarArchive.CreateInputTarArchive(gzipStream, Encoding.Default);
        tarArchive.ExtractContents(destFolder);
        tarArchive.Close();

        gzipStream.Close();
        inStream.Close();
    }

    public static string ComputeSha256Hash(string str)
    {
        return ComputeSha256Hash(Encoding.UTF8.GetBytes(str));
    }

    public static string ComputeFileHash(string filePath)
    {
        using var fileStream = File.OpenRead(filePath);
        using var sha256Hash = SHA256.Create();
        var bytes = sha256Hash.ComputeHash(fileStream);

        return ConvertBytesToString(bytes);
    }

    private static string ConvertBytesToString(byte[] bytes)
    {
        // Convert byte array to a string
        var builder = new StringBuilder();
        foreach (var t in bytes)
            builder.Append(t.ToString("x2"));

        return builder.ToString();
    }

    public static string ComputeSha256Hash(byte[] rawData)
    {
        using var sha256Hash = SHA256.Create();
        var bytes = sha256Hash.ComputeHash(rawData);

        return ConvertBytesToString(bytes);
    }

    private const string SmartFileCacheFolder = "SmartFileCache";

    /// <summary>
    /// Downloads a file using a rudimentary cache in temp folder
    /// </summary>
    /// <param name="url"></param>
    /// <param name="path"></param>
    public static void SmartDownloadFile(string url, string path)
    {
        var uri = new Uri(url);
        var fileName = uri.Segments.Last();

        var smartFileCacheFolder = Path.Combine(Path.GetTempPath(), SmartFileCacheFolder);

        if (!Directory.Exists(smartFileCacheFolder))
            Directory.CreateDirectory(smartFileCacheFolder);

        var cachedFilePath = Path.Combine(smartFileCacheFolder, fileName);

        if (!File.Exists(cachedFilePath))
            HttpHelper.DownloadFileAsync(url, cachedFilePath).Wait();

        File.Copy(cachedFilePath, path, true);
    }

    /// <summary>
    /// Downloads a file using a rudimentary cache in temp folder
    /// </summary>
    /// <param name="url"></param>
    public static byte[] SmartDownloadData(string url)
    {
        var tmp = Path.GetTempFileName();

        SmartDownloadFile(url, tmp);

        var data = File.ReadAllBytes(tmp);

        File.Delete(tmp);

        return data;
    }

    // Credit: https://stackoverflow.com/questions/12166404/how-do-i-get-folder-size-in-c
    public static long GetDirectorySize(string folderPath)
    {
        DirectoryInfo di = new DirectoryInfo(folderPath);
        return di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
    }

    public static StorageInfo GetStorageInfo(string path)
    {
        var f = new FileInfo(path);
        var drive = Path.GetPathRoot(f.FullName);

        // This could be improved but It's enough for now
        var info = DriveInfo.GetDrives()
            .FirstOrDefault(drv =>
                string.Equals(drv.Name, drive, StringComparison.OrdinalIgnoreCase));

        return info == null ? null : new StorageInfo(info.TotalSize, info.AvailableFreeSpace);
    }

    public static bool SafeDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool SafeCopy(string source, string dest, bool overwrite = true)
    {
        try
        {
            File.Copy(source, dest, overwrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool SafeDeleteFolder(string path)
    {
        try
        {
            Directory.Delete(path, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void RemoveEmptyFolders(string folder, bool removeSelf = false)
    {
        try
        {
            if (!Directory.Exists(folder)) return;

            // Recursive call
            Directory.EnumerateDirectories(folder).ToList().ForEach(f => RemoveEmptyFolders(f, true));

            // If not empty we don't have to delete it
            if (Directory.EnumerateFileSystemEntries(folder).Any()) return;

            if (!removeSelf) return;

            try
            {
                Directory.Delete(folder);
            }
            catch (UnauthorizedAccessException)
            {
                //
            }
            catch (DirectoryNotFoundException)
            {
                //
            }
        }
        catch (UnauthorizedAccessException)
        {
            //
        }
        catch (DirectoryNotFoundException)
        {
            //
        }
    }

    /// <summary>
    /// Combines an array of strings into a path using the forward slash as folder separator
    /// </summary>
    /// <param name="paths">An array of parts of the path.</param>
    /// <returns></returns>
    public static string SafeCombine(params string[] paths)
    {
        return Path.Combine(paths.Where(item => item != null).ToArray()).Replace('\\', '/');
    }

    public static (string, Stream) GetTempStream(int bufferSize = 104857600)
    {
        var file = Path.Combine(Path.GetTempPath(), "temp-files", RandomString(16));

        return (file, new BufferedStream(File.Open(file, FileMode.CreateNew, FileAccess.ReadWrite), bufferSize));
    }

    public static string[] SafeTreeDelete(string baseTempFolder, int rounds = 3, int delay = 500)
    {
        var entries = new List<string>(
            Directory.EnumerateFileSystemEntries(baseTempFolder, "*", SearchOption.AllDirectories));

        for (var n = 0; n < rounds; n++)
        {
            foreach (var entry in entries.ToArray())
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, true);
                        entries.Remove(entry);
                        continue;
                    }

                    if (File.Exists(entry))
                    {
                        File.Delete(entry);
                        entries.Remove(entry);
                        continue;
                    }

                    entries.Remove(entry);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Exception: " + ex.Message);
                }
            }

            if (entries.Count == 0)
                return [];

            Thread.Sleep(delay);
        }

        return entries.ToArray();
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
                return i.ToString("0 B"); // Byte
        }

        // Divide by 1024 to get fractional value
        readable /= 1024;
        // Return formatted number with suffix
        return readable.ToString("0.### ") + suffix;
    }

    public static bool Validate<T>(T obj, out ICollection<ValidationResult> results)
    {
        results = new List<ValidationResult>();

        return Validator.TryValidateObject(obj, new ValidationContext(obj), results, true);
    }

    public static string ToErrorString(this IEnumerable<ValidationResult> results)
    {
        var builder = new StringBuilder();

        foreach (var res in results)
        {
            builder.Append(res.ErrorMessage);

            if (res.MemberNames.Any())
            {
                builder.Append(" (");
                builder.Append(string.Join(", ", res.MemberNames));
                builder.Append(')');
            }

            builder.Append("; ");
        }

        return builder.ToString();
    }
    /*
    public static async Task<FileStream> WaitForFile(string fullPath, FileMode mode, FileAccess access, FileShare share,
        int hops = 15, int baseDelay = 10, int incrementDelay = 2)
    {
        int delay = baseDelay;

        for (var hop = 0; hop < hops; hops++)
        {
            FileStream fs = null;
            try
            {
                fs = new FileStream(fullPath, mode, access, share);
                return fs;
            }
            catch (IOException)
            {
                if (fs != null)
                {
                    await fs.DisposeAsync();
                }

                delay *= 2;
                await Task.Delay(delay);
            }
        }

        return null;
    }*/

    public static async Task<FileStream> WaitForFile(string fullPath, FileMode mode, FileAccess access,
        FileShare share,
        int delay = 50, int retries = 1200)
    {
        for (var numTries = 0; numTries < retries; numTries++)
        {
            FileStream fs = null;
            try
            {
                fs = new FileStream(fullPath, mode, access, share);
                return fs;
            }
            catch (IOException)
            {
                if (fs != null)
                {
                    await fs.DisposeAsync();
                }

                await Task.Delay(delay); // Thread.Sleep (50);
            }
        }

        return null;
    }

    public static bool IsFileAccessable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch
        {
            return false;
        }
    }


    public static void WriteLineColor(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public static void WriteColor(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }

    public static int? SafeParse(string str, IFormatProvider provider = null)
    {
        return int.TryParse(str, NumberStyles.Integer, provider, out var result) ? result : null;
    }

    public static void SetDefaultDllPath(string path)
    {
        const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            return;

        if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var dirHandle = AddDllDirectory(path);
        if (dirHandle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr AddDllDirectory([MarshalAs(UnmanagedType.LPWStr)] string lpPathName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetDefaultDllDirectories(uint flags);
    }


    public const string DroneDBDllNameWindows = "ddb.dll";
    public const string DroneDBDllNameLinux = "libddb.so";

    public static string FindDdbFolder()
    {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (path == null)
            return null;

        var newPath = path.Split(Path.PathSeparator);

        string libraryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? DroneDBDllNameWindows
            : DroneDBDllNameLinux;

        var ddbFolder = newPath.FirstOrDefault(
            p => File.Exists(Path.Combine(p, libraryName)));

        return ddbFolder;
    }


}