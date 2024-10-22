using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Registry.Common;

public static class FolderUtils
{

    // Credit https://stackoverflow.com/a/690980
    public static void Copy(string sourceDirectory, string targetDirectory, bool overwrite = false, string[] excludes = null)
    {
        var diSource = new DirectoryInfo(sourceDirectory);
        var diTarget = new DirectoryInfo(targetDirectory);

        CopyAll(diSource, diTarget, overwrite, excludes);
    }

    public static void CopyAll(DirectoryInfo source, DirectoryInfo target, bool overwrite = false, string[] excludes = null)
    {
        Directory.CreateDirectory(target.FullName);

        // Copy each file into the new directory.
        foreach (var fi in source.GetFiles())
        {
            if (excludes != null && excludes.Contains(fi.Name)) continue;

            var dest = Path.Combine(target.FullName, fi.Name);
            if (File.Exists(dest) && !overwrite) continue;

            fi.CopyTo(dest, overwrite);
        }

        // Copy each subdirectory using recursion.
        foreach (var diSourceSubDir in source.GetDirectories())
        {
            var nextTargetSubDir = target.CreateSubdirectory(diSourceSubDir.Name);

            CopyAll(diSourceSubDir, nextTargetSubDir, overwrite, excludes);
        }
    }

    public static void Move(string sourceDirectory, string targetDirectory, string[] excludes = null)
    {
        Copy(sourceDirectory, targetDirectory, true, excludes);
        Directory.Delete(sourceDirectory, true);
    }
}