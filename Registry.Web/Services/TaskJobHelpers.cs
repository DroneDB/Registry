#nullable enable
using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
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
    public static string? ResolveArtifactFile(string taskId, string tempPath)
    {
        var dir = ArtifactDirectory(taskId, tempPath);
        if (dir is null || !Directory.Exists(dir))
            return null;

        return Directory.EnumerateFiles(dir).FirstOrDefault();
    }

    /// <summary>
    /// Best-effort removal of a task's produced-artifact working directory. Failures are
    /// reported through <paramref name="logger"/> when supplied and otherwise ignored;
    /// they must never abort the surrounding delete.
    /// </summary>
    public static void TryDeleteArtifacts(string taskId, string tempPath, ILogger? logger = null)
    {
        try
        {
            var dir = ArtifactDirectory(taskId, tempPath);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to delete artifacts for task {TaskId}", taskId);
        }
    }

    /// <summary>Path-traversal-guarded <c>{tempPath}/tasks/{taskId}</c>, or null when the id escapes the root.</summary>
    private static string? ArtifactDirectory(string taskId, string tempPath)
    {
        var dir = Path.GetFullPath(Path.Combine(tempPath, ArtifactsFolder, taskId));
        var root = Path.GetFullPath(Path.Combine(tempPath, ArtifactsFolder));
        // Require a separator boundary so "tasks_evil" can't pass a bare prefix match
        return dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? dir : null;
    }
}
