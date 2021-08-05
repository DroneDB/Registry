using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
using Registry.Common.Model;
using ZipFile = System.IO.Compression.ZipFile;

namespace Registry.Common
{
    public static class CommonUtils
    {
        public static string RandomString(int length)
        {
            const string valid = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
            var res = new StringBuilder();
            using var rng = new RNGCryptoServiceProvider();
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
                fs1.Read(one, 0, BytesToRead);
                fs2.Read(two, 0, BytesToRead);

                if (BitConverter.ToInt64(one, 0) != BitConverter.ToInt64(two, 0))
                    return false;
            }

            return true;
        }

        public static TValue SafeGetValue<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
        {
            return !dictionary.TryGetValue(key, out var value) ? default : value;
        }

        public static TValueOut? SafeGetValue<TKey, TValue, TValueOut>(this IDictionary<TKey, TValue> dictionary,
            TKey key, Func<TValue, TValueOut> selector) where TValueOut : struct
        {
            return !dictionary.TryGetValue(key, out var value) ? null : (TValueOut?)selector(value);
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

                    if (!Directory.Exists(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }
                }
            }
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
            {
                var client = new WebClient();
                client.DownloadFile(url, cachedFilePath);
            }

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

        public static void RemoveEmptyFolders(string folder)
        {
            if (!Directory.Exists(folder)) return;

            foreach (var directory in Directory.GetDirectories(folder))
            {
                RemoveEmptyFolders(directory);
                if (!Directory.Exists(directory)) continue;
                if (Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory, false);
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
            return Path.Combine(paths.Where(item => item != null).ToArray()).Replace('\\', '/');
        }
        
        public static (string, Stream) GetTempStream(int bufferSize = 104857600)
        {
            var file = Path.Combine(Path.GetTempPath(), "temp-files", RandomString(16));

            return (file, new BufferedStream(File.Open(file, FileMode.CreateNew, FileAccess.ReadWrite), bufferSize));
        }

        public static bool SafeTreeDelete(string baseTempFolder, int rounds = 3, int delay = 500)
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

                if (!entries.Any()) return true;

                Thread.Sleep(delay);
            }

            return !entries.Any();
        }

        private static readonly HashSet<string> _compressibleMimeTypes =  new()
        { "text/html",
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
            "font/opentype"};

        public static CompressionLevel GetCompressionLevel(string path)
        {
            if (!MimeTypes.TryGetMimeType(path, out var mimeType))
                return CompressionLevel.NoCompression;
            
            return _compressibleMimeTypes.Contains(mimeType)
                ? CompressionLevel.Optimal
                : CompressionLevel.NoCompression;
        }
    }


}
