using System;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Services.HeavyTasks.Models;

namespace Registry.Web.Services.HeavyTasks.Ports;

/// <summary>What a tool tells the task's log besides its progress.</summary>
public static class HeavyToolProgressExtensions
{
    /// <summary>
    /// Runs a native call of a tool and, when it fails with an error of libddb (a <see cref="DdbException"/>, or the
    /// <see cref="InvalidOperationException"/> that wraps a plain one), writes the message to the log of the task and
    /// rethrows. A task that fails records only the type of its error, so this line is how the user learns why (the
    /// file libddb names, the format it does not convert). A cancellation and a transient error (a build lock held,
    /// a full disk) are left out: the job is stopped, or runs again.
    /// </summary>
    public static async Task<T> ReportingFailureAsync<T>(this IProgress<HeavyToolProgress> progress, Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (DdbException ex) when (!DdbErrors.IsTransient(ex))
        {
            progress.Report(Failure(ex));
            throw;
        }
        catch (InvalidOperationException ex)
        {
            progress.Report(Failure(ex));
            throw;
        }
    }

    private static HeavyToolProgress Failure(Exception error) =>
        new(-1, "failed", LogChunk: $"Failed: {error.Message}");
}
