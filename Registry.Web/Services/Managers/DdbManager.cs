using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Factory for IDDB instances; resolves dataset folder paths and creates DDB wrappers.
/// </summary>
public class DdbManager : IDdbManager
{
    private readonly ILogger<DdbManager> _logger;
    private readonly IDdbWrapper _ddbWrapper;
    private readonly StorageOptions _settings;
    private readonly InitializedDatasetFolders _initialized;

    public DdbManager(IOptions<StorageOptions> settings, ILogger<DdbManager> logger, IDdbWrapper ddbWrapper,
        InitializedDatasetFolders? initialized = null)
    {
        _logger = logger;
        _ddbWrapper = ddbWrapper;
        _settings = settings.Value;
        _initialized = initialized ?? new InitializedDatasetFolders();
    }

    // The options validator refuses to start without a datasets path
    private string DatasetsPath => _settings.DatasetsPath!;

    public IDDB Get(string orgSlug, Guid internalRef)
    {
        var baseDdbPath = GetDatasetFolder(orgSlug, internalRef);

        // Every request opens its dataset: check the folder and the index once per process, not
        // with two file system calls per open
        if (_initialized.Contains(baseDdbPath))
            return new DDB(baseDdbPath, _ddbWrapper);

        Directory.CreateDirectory(baseDdbPath);

        var ddb = new DDB(baseDdbPath, _ddbWrapper);

        // TODO: It would be nice if we could use the bindings to check this
        if (!Directory.Exists(Path.Combine(baseDdbPath, IDDB.DatabaseFolderName)))
        {
            ddb.Init();
            _logger.LogInformation("Initialized new ddb in '{DaseDdbPath}'", baseDdbPath);
        }
        else
            _logger.LogDebug("Opened ddb in '{BaseDdbPath}'", baseDdbPath);

        _initialized.Add(baseDdbPath);
        return ddb;
    }

    public IDDB Open(string orgSlug, Guid internalRef)
    {
        var baseDdbPath = GetDatasetFolder(orgSlug, internalRef);

        if (_initialized.Contains(baseDdbPath) || Directory.Exists(Path.Combine(baseDdbPath, IDDB.DatabaseFolderName)))
            return new DDB(baseDdbPath, _ddbWrapper);

        throw new DirectoryNotFoundException($"Dataset {orgSlug}/{internalRef} has no index (deleted?)");
    }

    public bool TryParseDatasetFolder(string? datasetFolderPath, out string orgSlug, out Guid internalRef)
    {
        orgSlug = string.Empty;
        internalRef = Guid.Empty;
        if (string.IsNullOrWhiteSpace(datasetFolderPath) || string.IsNullOrWhiteSpace(_settings.DatasetsPath))
            return false;

        var relative = Path.GetRelativePath(Path.GetFullPath(_settings.DatasetsPath),
            Path.GetFullPath(datasetFolderPath)).ToDdbPath().TrimEnd('/');
        var segments = relative.Split('/');
        if (segments.Length != 2 || segments[0] is "" or "." or ".." || !Guid.TryParse(segments[1], out internalRef))
            return false;

        orgSlug = segments[0];
        return true;
    }

    public void Delete(string orgSlug, Guid internalRef)
    {

        var baseDdbPath = GetDatasetFolder(orgSlug, internalRef);

        if (!Directory.Exists(baseDdbPath)) {
            _logger.LogWarning("Asked to remove the folder '{BaseDdbPath}' but it does not exist", baseDdbPath);
            return;
        }

        _logger.LogInformation("Removing ddb dataset '{BaseDdbPath}'", baseDdbPath);

        ReleaseConnections(baseDdbPath);
        Directory.Delete(baseDdbPath, true);

    }

    public void Delete(string orgSlug)
    {
        var orgPath = Path.Combine(DatasetsPath, orgSlug);

        if (!Directory.Exists(orgPath)) {
            _logger.LogWarning("Asked to remove the folder '{OrgPath}' but it does not exist", orgPath);
            return;
        }

        _logger.LogInformation("Removing ddb '{OrgPath}'", orgPath);

        ReleaseConnections(orgPath);
        Directory.Delete(orgPath, true);
    }

    public void ReleaseConnections(string folder)
    {
        // Every delete, move or wipe of dataset folders comes through here first
        _initialized.Forget(folder);

        // Best effort: the delete, move or wipe that follows must not fail because the index is
        // busy or libddb predates DDBReleaseConnections (only Windows refuses open files anyway)
        try
        {
            var closed = _ddbWrapper.ReleaseConnections(folder);
            if (closed > 0)
                _logger.LogDebug("Closed {Count} idle index connections under '{Folder}'", closed, folder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot release the index connections under '{Folder}'", folder);
        }
    }

    public string GetDatasetFolder(string orgSlug, Guid internalRef)
    {
        if (string.IsNullOrWhiteSpace(orgSlug))
            throw new ArgumentException("Organization slug cannot be null or empty");

        return Path.Combine(DatasetsPath, orgSlug, internalRef.ToString());

    }
}