using System;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Registry.Ports;

namespace Registry.Web.Services.Jobs;

/// <summary>Background file jobs: the delayed best-effort delete (artifact TTLs).</summary>
[AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public class FileJobs
{
    private readonly IFileSystem _fs;
    private readonly ILogger<FileJobs> _logger;

    public FileJobs(IFileSystem fs, ILogger<FileJobs> logger)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Deletes the file or folder <paramref name="path"/> if it exists; a failure is logged, not thrown.</summary>
    public void Delete(string path, PerformContext? context)
    {
        var console = JobConsole.For(_logger, context);
        console.Debug("In Delete({Path})", path);

        if (!_fs.Exists(path) && !_fs.FolderExists(path))
        {
            console.Debug("No file or folder found");
            return;
        }

        console.Debug(_fs.TryDelete(path, ex => console.Warning(ex, "Cannot delete: {Error}", ex.Message))
            ? "Deleted successfully"
            : "Cannot delete");
    }
}
