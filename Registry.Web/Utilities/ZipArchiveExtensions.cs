using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Registry.Common;

namespace Registry.Web.Utilities;

public static class ZipArchiveExtension
{

    public static void CreateEntryFromAny(this ZipArchive archive, string sourceName, string entryName = "", string[] excludes = null)
    {
        var fileName = Path.GetFileName(sourceName);

        if (excludes != null && excludes.Contains(fileName)) return;

        var path = CommonUtils.SafeCombine(entryName, fileName);
            
        if (File.GetAttributes(sourceName).HasFlag(FileAttributes.Directory))
        {
            archive.CreateEntryFromDirectory(sourceName, path, excludes);
        }
        else
        {
            archive.CreateEntryFromFile(sourceName, path, CommonUtils.GetCompressionLevel(path));
        }
    }

    public static void CreateEntryFromDirectory(this ZipArchive archive, string sourceDirName, string entryName = "", string[] excludes = null)
    {
        var files = Directory.GetFiles(sourceDirName).Concat(Directory.GetDirectories(sourceDirName)).ToArray();
            
        foreach (var file in files)
        {
            if (excludes != null && excludes.Contains(file)) return;
            archive.CreateEntryFromAny(file, entryName, excludes);
        }
    }
}