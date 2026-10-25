#nullable enable
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>The files a task produced, under the scratch folder (out of <c>TasksManager</c>).</summary>
public sealed class TaskArtifactStore
{
    private readonly IFileSystem _fs;
    private readonly string _tempPath;
    private readonly ILogger<TaskArtifactStore> _logger;

    public TaskArtifactStore(IFileSystem fs, ScratchFileProvider scratch, ILogger<TaskArtifactStore> logger)
    {
        _fs = fs;
        _tempPath = scratch.Root;
        _logger = logger;
    }

    /// <summary>The single file task <paramref name="taskId"/> produced, or null when it is gone (expired or cleaned up).</summary>
    public string? Resolve(string taskId) => TaskJobHelpers.ResolveArtifactFile(_fs, taskId, _tempPath);

    /// <summary>Best-effort removal of the work directory of task <paramref name="taskId"/>; a failure is logged and never thrown.</summary>
    public void Delete(string taskId) => TaskJobHelpers.DeleteArtifacts(_fs, taskId, _tempPath, _logger);
}
