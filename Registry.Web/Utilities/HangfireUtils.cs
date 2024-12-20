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
using Registry.Web.Attributes;
using Serilog;

namespace Registry.Web.Utilities;

public static class HangfireUtils
{
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public static void BuildWrapper(IDDB ddb, string path, bool force,
        PerformContext context)
    {
        Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

        writeLine($"In BuildWrapper('{ddb.DatasetFolderPath}', '{path}', '{force}')");

        writeLine("Running build");
        ddb.Build(path, force: force);

        writeLine("Done build");
    }

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public static void BuildPendingWrapper(IDDB ddb, PerformContext context)
    {
        Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

        writeLine($"In BuildPendingWrapper('{ddb.DatasetFolderPath}')");

        writeLine("Running build pending");
        ddb.BuildPending();

        writeLine("Done build pending");
    }

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public static void GenerateThumbnailWrapper(IDDB ddb, string path, int size, string dest,
        PerformContext context)
    {
        Action<string> writeLine = context != null ? context.WriteLine : Log.Information;

        writeLine($"In GenerateThumbnailWrapper('{ddb.DatasetFolderPath}', '{path}', '{size}', '{dest}')");

        writeLine("Running generate thumbnail");
        var result = ddb.GenerateThumbnail(path, size);

        if (result != null)
        {
            writeLine("Saving thumbnail");
            File.WriteAllBytes(dest, result);
        }
        else
        {
            writeLine("Thumbnail generation failed");
        }

        writeLine("Done generate thumbnail");
    }

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
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

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
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

    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [JobExpiration(ExpirationTimeoutInMinutes = 5)]
    public static void DummyJob(PerformContext context)
    {
        using var connection = JobStorage.Current.GetConnection();

        Action<string> writeLine = context != null ? context.WriteLine : Log.Information;
        writeLine("Dummy job");
    }
}