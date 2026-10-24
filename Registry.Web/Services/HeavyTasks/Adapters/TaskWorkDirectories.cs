#nullable enable
using System;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>The work folder of a task that produces an artifact (RQ-12, out of <c>HeavyTaskJobWrapper</c>).</summary>
public sealed class TaskWorkDirectories
{
    private readonly string _tempPath;
    private readonly IFileSystem _fs;
    private readonly ILogger<TaskWorkDirectories> _logger;

    public TaskWorkDirectories(ScratchFileProvider scratch, IFileSystem fs, ILogger<TaskWorkDirectories> logger)
    {
        _tempPath = scratch.Root;
        _fs = fs;
        _logger = logger;
    }

    /// <summary><c>{scratch}/tasks/{taskId}</c> when the task produces an artifact, else null.</summary>
    /// <exception cref="InvalidOperationException">The task id escapes the tasks folder.</exception>
    public string? PathFor(string taskId, bool producesArtifact) =>
        producesArtifact
            ? TaskJobHelpers.ArtifactDirectory(taskId, _tempPath)
              ?? throw new InvalidOperationException($"Invalid task id '{taskId}'.")
            : null;

    /// <summary>Creates the folder.</summary>
    public void Create(string workDir) => _fs.FolderCreate(workDir);

    /// <summary>Best-effort deletion of the folder (null is nothing to delete); a failure is logged.</summary>
    public void Delete(string? workDir) =>
        _fs.TryDelete(workDir, ex => _logger.LogWarning(ex, "Failed to delete task work directory '{Dir}'", workDir));
}
