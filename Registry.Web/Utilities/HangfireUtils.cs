﻿using System;
using System.IO;
using System.Linq;
using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Hangfire.Storage;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports.DroneDB;
using Serilog;

namespace Registry.Web.Utilities
{
    public static class HangfireUtils
    {
        public static void BuildWrapper(IDDB ddb, string path, bool force,
            PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

            writeLine($"In BuildWrapper('{ddb.DatasetFolderPath}', '{path}', '{force}')");

            writeLine("Running build");
            ddb.Build(path, force: force);

            writeLine("Done build");
        }

        public static void BuildPendingWrapper(IDDB ddb, PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

            writeLine($"In BuildPendingWrapper('{ddb.DatasetFolderPath}')");

            writeLine("Running build pending");
            ddb.BuildPending();

            writeLine("Done build pending");
        }

        public static void SafeDelete(string path, PerformContext context)
        {
            Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

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

        public static void CleanupExpiredJobs(PerformContext context)
        {
            using var connection = JobStorage.Current.GetConnection();

            Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

            var toDelete = connection.GetRecurringJobs()
                .Where(j => j.LastJobState == "Failed" && j.CreatedAt < DateTime.Now.AddDays(-30))
                .ToList();

            writeLine($"Found {toDelete.Count} jobs to delete");

            foreach (var job in toDelete)
            {
                writeLine($"Deleting job {job.Id}");
                RecurringJob.RemoveIfExists(job.Id);
            }
        }
    }
}