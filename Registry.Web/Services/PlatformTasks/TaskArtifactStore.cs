#nullable enable
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>The files a task produced, under the scratch folder (RQ-12, out of <c>TasksManager</c>).</summary>
public interface ITaskArtifactStore
{
    /// <summary>The single file task <paramref name="taskId"/> produced, or null when it is gone (expired or cleaned up).</summary>
    string? Resolve(string taskId);

    /// <summary>Best-effort removal of the work directory of task <paramref name="taskId"/>; a failure is logged and never thrown.</summary>
    void Delete(string taskId);
}

/// <summary>Default <see cref="ITaskArtifactStore"/>: <c>{scratch}/tasks/{taskId}</c> (<see cref="TaskJobHelpers"/>).</summary>
public sealed class TaskArtifactStore : ITaskArtifactStore
{
    private readonly IFileSystem _fs;
    private readonly string _tempPath;
    private readonly ILogger<TaskArtifactStore> _logger;

    public TaskArtifactStore(IFileSystem fs, IScratchFileProvider scratch, ILogger<TaskArtifactStore> logger)
    {
        _fs = fs;
        _tempPath = scratch.Root;
        _logger = logger;
    }

    public string? Resolve(string taskId) => TaskJobHelpers.ResolveArtifactFile(_fs, taskId, _tempPath);

    public void Delete(string taskId) => TaskJobHelpers.DeleteArtifacts(_fs, taskId, _tempPath, _logger);
}
