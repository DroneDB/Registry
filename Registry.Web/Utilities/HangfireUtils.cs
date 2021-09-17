using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hangfire.Console;
using Hangfire.Server;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using Registry.Ports.ObjectSystem;

namespace Registry.Web.Utilities
{
    public static class HangfireUtils
    {

        public static void BuildWrapper(string ddbPath, string path, string tempFile, bool force, PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Console.WriteLine;

            writeLine($"In BuildWrapper('{ddbPath}', '{path}', '{tempFile}')");

            // TODO: This could be a violation of our abstraction. We should serialize the IDdb object and pass it to this method
            var ddb = new Ddb(ddbPath);

            var folderPath = Path.Combine(ddbPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(folderPath)!);

            writeLine($"Created folder structure");

            File.Copy(tempFile, folderPath, true);

            writeLine("Temp file copied");

            writeLine("Running build");
            ddb.Build(path, null, force);

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
                writeLine("File does not exist");
        }


        public static void SyncBuildFolder(IObjectSystem objectSystem, IDdb ddb, DdbEntry obj, string bucketName, PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Console.WriteLine;

            // TODO: We are assuming this convention, if ddb changes this policy we are screwed
            var buildPath = Path.Combine(ddb.BuildFolder, obj.Hash);
            var destFolder = Path.Combine("." + (Path.GetFileName(ddb.BuildFolder) ?? "build"), obj.Hash);

            writeLine($"SyncBuildFolder -> '{buildPath}' to '{destFolder}'");

            // Put it on storage
            SyncFolder(objectSystem, buildPath, bucketName, destFolder, context);

        }

        public static void SyncFolder(IObjectSystem objectSystem, string sourcePath, string bucketName, string destPath, PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Console.WriteLine;

            writeLine($"Using bucket '{bucketName}'");

            foreach (var file in Directory.EnumerateFiles(sourcePath))
            {
                var name = Path.GetFileName(file);
                var source = Path.Combine(sourcePath, name);
                var dest = Path.Combine(destPath, name);

                var contentType = MimeTypes.GetMimeType(file);

                writeLine($"'{file}' -> PutObjectAsync('{name}', '{source}', '{dest}', '{contentType}')");

                objectSystem.PutObjectAsync(bucketName, dest, source, contentType).Wait();
            }

            foreach (var folder in Directory.EnumerateDirectories(sourcePath))
            {
                var name = Path.GetFileName(folder);

                var source = Path.Combine(sourcePath, name);
                var dest = Path.Combine(destPath, name);

                writeLine($"'{folder}' -> SyncFolder('{name}', '{source}', '{dest}')");

                SyncFolder(objectSystem, source, bucketName, dest, context);

            }
        }


    }
}
