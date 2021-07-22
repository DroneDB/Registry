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

namespace Registry.Web.Utilities
{
    public static class HangfireUtils
    {

        public static void BuildWrapper(string ddbPath, string path, string tempFile, PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Console.WriteLine;

            writeLine($"In BuildWrapper('{ddbPath}', '{path}', '{tempFile}')");

            var ddb = new Ddb(ddbPath);

            var folderPath = Path.Combine(ddbPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(folderPath)!);

            writeLine($"Created folder structure");

            File.Copy(tempFile, folderPath);

            writeLine("Temp file copied");

            writeLine("Running build");
            ddb.Build(path);

            writeLine("Done build");

            File.Delete(folderPath);

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
    }
}
