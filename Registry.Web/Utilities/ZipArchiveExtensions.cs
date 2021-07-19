using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Registry.Common;

namespace Registry.Web.Utilities
{
    public static class ZipArchiveExtension
    {

        public static void CreateEntryFromAny(this ZipArchive archive, string sourceName, string entryName = "")
        {
            var fileName = Path.GetFileName(sourceName);
            if (File.GetAttributes(sourceName).HasFlag(FileAttributes.Directory))
            {
                archive.CreateEntryFromDirectory(sourceName, CommonUtils.SafeCombine(entryName, fileName));
            }
            else
            {
                archive.CreateEntryFromFile(sourceName, CommonUtils.SafeCombine(entryName, fileName), CompressionLevel.NoCompression);
            }
        }

        public static void CreateEntryFromDirectory(this ZipArchive archive, string sourceDirName, string entryName = "")
        {
            var files = Directory.GetFiles(sourceDirName).Concat(Directory.GetDirectories(sourceDirName)).ToArray();
            //archive.CreateEntry(Path.Combine(entryName, Path.GetFileName(sourceDirName)));
            foreach (var file in files)
            {
                archive.CreateEntryFromAny(file, entryName);
            }
        }
    }
}
