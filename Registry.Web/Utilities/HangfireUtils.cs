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
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Utilities
{
    public static class HangfireUtils
    {

        public static void BuildWrapper(IDdb ddb, string path, bool force,
            PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Console.WriteLine;

            writeLine($"In BuildWrapper('{ddb.DatasetFolderPath}', '{path}', '{force}')");

            writeLine("Running build");
            ddb.Build(path, force: force);

            writeLine("Done build");
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
                }
                else
                    writeLine("No file or folder found");
            }
        }

    }
}