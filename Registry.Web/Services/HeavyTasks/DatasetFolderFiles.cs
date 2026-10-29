using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// The files of a dataset folder as a task that writes into it sees them (out of <c>ImportDatasetTool</c>):
/// what was there before the task, what it added, and the removal of what it added when it must be rolled back.
/// Paths are the dataset's: '/' separated and relative to the folder, without any ".ddb" folder (libddb never
/// indexes it) and without the root ".uploads" folder, whose in-flight files must not be indexed or rolled back
/// (<see cref="DatasetPathPolicy.EnumerateFiles"/>).
/// </summary>
public sealed class DatasetFolderFiles
{
    private readonly IFileSystem _fs;
    private readonly DatasetPathPolicy _paths;
    private readonly ILogger<DatasetFolderFiles> _logger;

    public DatasetFolderFiles(IFileSystem fs, DatasetPathPolicy paths, ILogger<DatasetFolderFiles> logger)
    {
        _fs = fs;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Creates <paramref name="root"/> when missing and returns the files already in it.</summary>
    public IReadOnlySet<string> Prepare(string root)
    {
        _fs.FolderCreate(root);
        return All(root).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every file in <paramref name="root"/>.</summary>
    public IEnumerable<string> All(string root) => _paths.EnumerateFiles(_fs, root);

    /// <summary>The files in <paramref name="root"/> that are not in <paramref name="before"/>.</summary>
    public List<string> AddedSince(string root, IReadOnlySet<string> before) =>
        [.. All(root).Where(f => !before.Contains(f))];

    /// <summary>Deletes <paramref name="relativePaths"/>; one that cannot be deleted is logged and the others still are.</summary>
    public void Delete(string root, IEnumerable<string> relativePaths)
    {
        foreach (var rel in relativePaths)
        {
            try
            {
                var local = DatasetPathPolicy.ToLocalPath(root, rel);
                if (_fs.Exists(local))
                    _fs.Delete(local);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete partially imported file '{Path}'", rel);
            }
        }
    }
}
