using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Registry.Common
{
    public static class FolderUtils
    {

        // Credit https://stackoverflow.com/a/690980
        public static void Copy(string sourceDirectory, string targetDirectory)
        {
            var diSource = new DirectoryInfo(sourceDirectory);
            var diTarget = new DirectoryInfo(targetDirectory);

            CopyAll(diSource, diTarget);
        }

        public static void CopyAll(DirectoryInfo source, DirectoryInfo target)
        {
            Directory.CreateDirectory(target.FullName);

            // Copy each file into the new directory.
            foreach (var fi in source.GetFiles())
                fi.CopyTo(Path.Combine(target.FullName, fi.Name), true);
            
            // Copy each subdirectory using recursion.
            foreach (var diSourceSubDir in source.GetDirectories())
            {
                var nextTargetSubDir =
                    target.CreateSubdirectory(diSourceSubDir.Name);
                CopyAll(diSourceSubDir, nextTargetSubDir);
            }
        }

        public static void Move(string sourceDirectory, string targetDirectory)
        {
            Copy(sourceDirectory, targetDirectory);
            Directory.Delete(sourceDirectory, true);
        }
    }
}
