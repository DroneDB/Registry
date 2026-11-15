using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Maintenance;
using Registry.Web.Services.Storage;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Where the upgrade rescans (<see cref="UpgradeRescanStartupTask"/>) keep their progress,
/// <see cref="MagicStrings.UpgradeRescansFileName"/> in the storage folder, and the lock that lets one instance on that
/// storage run them, <see cref="MagicStrings.UpgradeRescansLockFileName"/> held open with an exclusive OS lock.
/// </summary>
internal sealed class UpgradeRescanProgress
{
    private readonly StorageDocument<UpgradeRescanState> _state;
    private readonly IFileSystem _fs;
    private readonly string _lockPath;
    private readonly ILogger<UpgradeRescanProgress> _logger;

    public UpgradeRescanProgress(IOptions<StorageOptions> storage, IFileSystem fs, ILogger<UpgradeRescanProgress> logger)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _state = new StorageDocument<UpgradeRescanState>(storage, fs, MagicStrings.UpgradeRescansFileName, logger);
        _lockPath = Path.Combine(storage.Value.StoragePath ?? ".", MagicStrings.UpgradeRescansLockFileName);
    }

    /// <summary>
    /// Opens the lock file for this instance alone (an exclusive lock the OS releases if the process dies); null when
    /// another instance holds it, or it cannot be opened: the job is skipped and the next start tries again. The lock
    /// is a real file of the OS: the stream holds the lock, so it is opened directly, not through the file system port.
    /// </summary>
    public FileStream? TryLock()
    {
        try
        {
            _fs.FolderCreate(Path.GetDirectoryName(Path.GetFullPath(_lockPath))!);
            return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            _logger.LogInformation("Upgrade rescans skipped: another instance runs them ({Path}: {Error}); " +
                                   "the next start tries again", _lockPath, ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Upgrade rescans skipped: cannot open {Path}; the next start tries again", _lockPath);
            return null;
        }
    }

    /// <summary>The progress recorded (none recorded: an empty one).</summary>
    public UpgradeRescanState Read() => (_state.Read() ?? new UpgradeRescanState()).Normalize();

    /// <summary>
    /// Records <paramref name="state"/>. A write that fails is logged: the datasets it did not record are rescanned
    /// again at the next start, which is harmless.
    /// </summary>
    public void Write(UpgradeRescanState state)
    {
        try
        {
            _state.Write(state);
        }
        catch (StorageDocumentException ex)
        {
            _logger.LogWarning(ex, "Cannot record the upgrade rescans done");
        }
    }
}
