using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Jobs;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// The dataset folder as the reconciliation sweep (<see cref="IndexReconciliationService"/>) sees it: the files a
/// dataset holds on disk, through the one dataset walker, and its quarantined uploads (the <c>.uploads/quarantine</c>
/// folder of <see cref="Managers.ObjectsManager"/>'s compensation), aged out after
/// <see cref="ReconciliationSettings.QuarantineRetentionDays"/>.
/// </summary>
public sealed class ReconciliationDisk
{
    private readonly IFileSystem _fs;
    private readonly DatasetPathPolicy _paths;
    private readonly ReconciliationSettings _settings;

    public ReconciliationDisk(IFileSystem fs, DatasetPathPolicy paths, IOptions<ReconciliationSettings> settings)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Value;
    }

    /// <summary>
    /// The files of the dataset folder <paramref name="datasetRoot"/> (<see cref="DatasetPathPolicy.EnumerateFiles"/>:
    /// the reserved folders are pruned during the walk).
    /// </summary>
    public IEnumerable<string> Files(string datasetRoot) => _paths.EnumerateFiles(_fs, datasetRoot);

    /// <summary>Deletes the quarantined uploads of <paramref name="datasetRoot"/> older than the retention; how many went.</summary>
    public int AgeOutQuarantine(string datasetRoot, JobConsole console)
    {
        var quarantineDir = Path.Combine(datasetRoot, IDDB.UploadsFolderName, IDDB.QuarantineFolderName);
        if (!_fs.FolderExists(quarantineDir))
            return 0;

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, _settings.QuarantineRetentionDays));
        var removed = 0;

        foreach (var file in _fs.EnumerateFiles(quarantineDir))
        {
            try
            {
                if (_fs.GetLastWriteTime(file).ToUniversalTime() < cutoff)
                {
                    _fs.Delete(file);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                console.Info("Failed to remove aged quarantine file '{File}': {Error}", file, ex.Message);
            }
        }

        return removed;
    }
}
