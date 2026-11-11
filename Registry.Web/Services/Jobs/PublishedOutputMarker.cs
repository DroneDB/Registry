using System;
using System.IO;
using Hangfire.Server;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// The job parameter that records that a job has written its output file (<see cref="Key"/>). A job that
/// writes a file and then indexes it can fail after the write (the index is busy) and be run again by its
/// retry policy or by a user retry: the next attempt reads the marker and goes straight to the indexing,
/// instead of refusing its own output as an existing file or writing it a second time.
/// </summary>
public static class PublishedOutputMarker
{
    /// <summary>Name of the job parameter: the dataset path of the written output.</summary>
    public const string Key = "outputPublished";

    /// <summary>
    /// The output an earlier attempt of the job wrote, or null.
    /// </summary>
    /// <param name="context">The running job (null outside Hangfire: no earlier attempt).</param>
    /// <returns>The dataset path recorded by <see cref="Set"/>, or null.</returns>
    public static string? Get(PerformContext? context)
    {
        if (context?.Connection is null || context.BackgroundJob?.Id is not { Length: > 0 } jobId)
            return null;
        var value = context.Connection.GetJobParameter(jobId, Key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Records that the job wrote <paramref name="path"/>. Called right after the write returns, before anything
    /// that can fail.
    /// </summary>
    /// <param name="context">The running job (null outside Hangfire: nothing is recorded).</param>
    /// <param name="path">Dataset path of the written output.</param>
    public static void Set(PerformContext? context, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (context?.Connection is null || context.BackgroundJob?.Id is not { Length: > 0 } jobId)
            return;
        context.Connection.SetJobParameter(jobId, Key, path);
    }

    /// <summary>
    /// Whether an earlier attempt wrote the output <paramref name="path"/> and it is still there: <paramref name="published"/>
    /// names it (without regard to case, D-040) and its file exists. A marker whose file is gone (deleted since) is
    /// ignored: the job writes the output again.
    /// </summary>
    /// <param name="ddb">The dataset of the job.</param>
    /// <param name="published">The marker (<see cref="Get"/>), or null.</param>
    /// <param name="path">The output of the job.</param>
    /// <returns>True when the job only has to index what the earlier attempt wrote.</returns>
    public static bool Covers(IDDB ddb, string? published, string path)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        if (published is null || !string.Equals(published, path, StringComparison.OrdinalIgnoreCase))
            return false;
        var local = ddb.GetLocalPath(published);
        return !string.IsNullOrEmpty(local) && File.Exists(local);
    }
}
