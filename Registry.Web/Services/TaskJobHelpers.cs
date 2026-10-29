using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Data.Models;

namespace Registry.Web.Services;

/// <summary>
/// Shared JobIndex projections and produced-artifact filesystem helpers, used by both the
/// per-dataset tasks manager and the admin tasks manager so the two cannot drift apart.
/// </summary>
public static class TaskJobHelpers
{
    private const string ArtifactsFolder = "tasks";

    /// <summary>Instant a task concluded, whichever terminal transition it took.</summary>
    public static DateTime? FinishedAt(JobIndex j) =>
        j.SucceededAtUtc ?? j.FailedAtUtc ?? j.DeletedAtUtc;

    /// <summary>
    /// Server-authoritative expiry of a produced artifact: the work directory is swept
    /// <paramref name="artifactTtlHours"/> after completion (see <c>HeavyTaskJobWrapper</c>).
    /// Null when the task has no downloadable artifact. Clients hide the download control
    /// once this instant passes so they never offer a 404 link.
    /// </summary>
    public static DateTime? ArtifactExpiresAt(JobIndex j, int artifactTtlHours) =>
        j.CurrentState == "Succeeded" && j.ArtifactSizeBytes is not null && j.SucceededAtUtc is { } finished
            ? finished.AddHours(Math.Max(1, artifactTtlHours))
            : null;

    /// <summary>
    /// Resolves the single file a task produced under <c>{tempPath}/tasks/{taskId}</c>,
    /// or null when the directory is gone (expired or cleaned up).
    /// </summary>
    public static string? ResolveArtifactFile(IFileSystem fs, string taskId, string tempPath)
    {
        var dir = ArtifactDirectory(taskId, tempPath);
        if (dir is null || !fs.FolderExists(dir))
            return null;

        return fs.EnumerateFiles(dir).FirstOrDefault();
    }

    /// <summary>
    /// Best-effort removal of a task's produced-artifact working directory
    /// (<see cref="FileSystemExtensions.TryDelete"/>). A failure is logged when a
    /// <paramref name="logger"/> is given and never aborts the surrounding delete.
    /// </summary>
    public static void DeleteArtifacts(IFileSystem fs, string taskId, string tempPath, ILogger? logger = null) =>
        fs.TryDelete(ArtifactDirectory(taskId, tempPath),
            ex => logger?.LogWarning(ex, "Failed to delete artifacts for task {TaskId}", taskId));

    /// <summary>
    /// <c>{tempPath}/tasks/{taskId}</c>, the work directory of a task that produces an artifact, or
    /// null when the id escapes <c>{tempPath}/tasks</c> (<see cref="CommonUtils.IsUnder"/>).
    /// </summary>
    public static string? ArtifactDirectory(string taskId, string tempPath)
    {
        var root = Path.Combine(tempPath, ArtifactsFolder);
        var dir = Path.GetFullPath(Path.Combine(root, taskId));
        return CommonUtils.IsUnder(root, dir) ? dir : null;
    }
}
