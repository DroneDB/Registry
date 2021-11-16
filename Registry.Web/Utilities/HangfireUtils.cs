using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DDB.Bindings;
using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Registry.Adapters.DroneDB;
using Registry.Adapters.ObjectSystem;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Utilities
{
    public static class HangfireUtils
    {
/*
        [AutomaticRetry(Attempts = 0, LogEvents = false, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
        public static void SyncAndCleanupWrapper(IObjectSystem objectSystem, PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Console.WriteLine;

            writeLine($"CleanupWrapper");

            if (objectSystem is CachedS3ObjectSystem system)
            {
                writeLine("Synchronizing cache");
                try
                {
                    system.Sync();
                }
                catch (Exception ex)
                {
                    writeLine("Cannot sync: " + ex.Message);
                }
            }

            try
            {
                writeLine("Running cleanup");

                objectSystem.Cleanup();
            }
            catch (Exception ex)
            {
                writeLine("Cannot sync: " + ex.Message);
            }

        }*/
        
        public static void BuildWrapper(IDdb ddb, string path, string tempFile, string dest, bool force, PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Console.WriteLine;

            writeLine($"In BuildWrapper('{ddb.DatasetFolderPath}', '{path}', '{tempFile}', '{dest}', '{force}')");

            var folderPath = Path.Combine(ddb.DatasetFolderPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(folderPath)!);

            writeLine($"Created folder structure: '{folderPath}'");

            File.Copy(tempFile, folderPath, true);
            writeLine("Temp file copied");

            writeLine("Running build");
            ddb.Build(path, dest, force);
            
            writeLine("Done build");

            SafeDelete(folderPath, context);

            writeLine("Deleted copy of temp file");
            writeLine("Done");

        }

        public static void SafeDelete(string path, PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Console.WriteLine;

            writeLine($"In SafeDelete('{path}')");

            if (File.Exists(path))
            {
                var res = CommonUtils.SafeDelete(path);
                writeLine(res ? "File deleted successfully" : "Cannot delete file");
            }
            else
            {
                if (Directory.Exists(path))
                {
                    writeLine(!CommonUtils.SafeDeleteFolder(path)
                        ? "Cannot delete folder"
                        : "Folder deleted successfully");
                } else 
                    writeLine("No file or folder found");
            }
        }

/*
        public static void SyncBuildFolder(IObjectSystem objectSystem, IDdb ddb, DdbEntry obj, string bucketName, PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Console.WriteLine;

            // TODO: We are assuming this convention, if ddb changes this policy we are screwed
            var buildPath = Path.GetFullPath(Path.Combine(ddb.BuildFolderPath, obj.Hash));
            var destFolder = CommonUtils.SafeCombine(ddb.DatabaseFolderName, ddb.BuildFolderName, obj.Hash);

            writeLine($"SyncBuildFolder -> '{buildPath}' to '{destFolder}'");

            // Put it on storage
            SyncFolder(objectSystem, buildPath, bucketName, destFolder, context);

        }

        public static void SyncFolder(IObjectSystem objectSystem, string sourcePath, string bucketName, string destPath, PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Console.WriteLine;

            writeLine($"SyncFolder -> '{sourcePath}' to '{destPath}' on bucket '{bucketName}");

            var cnt = 0;

            foreach (var file in Directory.EnumerateFiles(sourcePath))
            {
                var name = Path.GetFileName(file);
                var source = Path.GetFullPath(Path.Combine(sourcePath, name));
                var dest = CommonUtils.SafeCombine(destPath, name);

                var contentType = MimeTypes.GetMimeType(file);

#if DEBUG
                writeLine($"'{file}' -> PutObjectAsync('{name}', '{source}', '{dest}', '{contentType}')");
#endif
                // Retry if it fails. After N tries throw and exception. Hangfire will retry the job later
                Policies.Base.Execute(async () => 
                    await objectSystem.PutObjectAsync(bucketName, dest, source, contentType));

                cnt++;
            }

            writeLine($"Synced {cnt} files");

            foreach (var folder in Directory.EnumerateDirectories(sourcePath))
            {
                var name = Path.GetFileName(folder);

                var source = Path.Combine(sourcePath, name);
                var dest = CommonUtils.SafeCombine(destPath, name);

                writeLine($"'{folder}' -> SyncFolder('{name}', '{source}', '{dest}')");

                SyncFolder(objectSystem, source, bucketName, dest, context);

            }
        }
*/

    }
}
