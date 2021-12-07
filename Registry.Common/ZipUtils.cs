using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Registry.Common
{
    public static class ZipUtils
    {
        public static void CreateFromDirectory(
            string sourceDirectoryName, string destinationArchiveFileName, CompressionLevel compressionLevel,
            bool includeBaseDirectory, Encoding entryNameEncoding, Func<string, bool> exclude
        )
        {
            if (string.IsNullOrEmpty(sourceDirectoryName))
                throw new ArgumentNullException(nameof(sourceDirectoryName));

            if (string.IsNullOrEmpty(destinationArchiveFileName))
                throw new ArgumentNullException(nameof(destinationArchiveFileName));

            var filesToAdd = Directory.GetFiles(sourceDirectoryName, "*", SearchOption.AllDirectories);
            var entryNames = GetEntryNames(filesToAdd, sourceDirectoryName, includeBaseDirectory);

            using var zipFileStream = new FileStream(destinationArchiveFileName, FileMode.Create);
            using var archive = new ZipArchive(zipFileStream, ZipArchiveMode.Create, false, entryNameEncoding);

            for (var i = 0; i < filesToAdd.Length; i++)
            {
                var file = filesToAdd[i];

                // Add the following condition to do filtering:
                if (exclude(file))
                    continue;

                archive.CreateEntryFromFile(file, entryNames[i], compressionLevel);
            }
        }

        private static string[] GetEntryNames(string[] names, string sourceFolder, bool includeBaseName)
        {
            if (names == null || names.Length == 0)
                return Array.Empty<string>();

            if (includeBaseName)
                sourceFolder = Path.GetDirectoryName(sourceFolder);

            var length = string.IsNullOrEmpty(sourceFolder) ? 0 : sourceFolder.Length;
            if (length > 0 && sourceFolder != null && sourceFolder[length - 1] != Path.DirectorySeparatorChar &&
                sourceFolder[length - 1] != Path.AltDirectorySeparatorChar)
                length++;

            var result = new string[names.Length];

            for (var i = 0; i < names.Length; i++)
                result[i] = names[i][length..];

            return result;
        }

    }
}